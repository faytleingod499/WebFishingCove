/*
   Copyright 2024 DrMeepso

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/

namespace Cove.GodotFormat
{

    public class GodotReader
    {

        public byte[] data;
        private BinaryReader reader;
        public Serilog.Core.Logger logger;

        // Packets are attacker controlled. Deeply nested containers would overflow the stack, and a stack
        // overflow cannot be caught in .NET, so it kills the whole server. Legit packets nest ~4 levels.
        private const int MaxDepth = 32;
        private int depth = 0;

        public GodotReader(byte[] data, Serilog.Core.Logger serverLogger)
        {
            this.data = data;
            reader = new BinaryReader(new MemoryStream(data), System.Text.Encoding.UTF8);
            logger = serverLogger;
        }

        private long remainingBytes => reader.BaseStream.Length - reader.BaseStream.Position;

        private static readonly object badLock = new();
        private static long badWindowStart;
        private static int badLogged;
        private static int badSkipped;

        private void LogBadPacket(Exception e)
        {
            lock (badLock)
            {
                long now = Environment.TickCount64;
                if (now - badWindowStart > 60_000)
                {
                    if (badSkipped > 0)
                        logger.Warning($"{badSkipped} more unreadable packets in the last minute were not logged.");
                    badWindowStart = now;
                    badLogged = 0;
                    badSkipped = 0;
                }
                if (badLogged < 5)
                {
                    badLogged++;
                    string what = e.Message.Length > 120 ? e.Message.Substring(0, 120) + "..." : e.Message;
                    logger.Warning($"Unreadable packet ({e.GetType().Name}: {what})");
                }
                else
                {
                    badSkipped++;
                }
            }
        }

        public Dictionary<string, object> readPacket()
        {
            Dictionary<string, object> dic = new Dictionary<string, object>();

            try
            {
                int type = reader.ReadInt32();

                if (type != (int)GodotTypes.dictionaryValue)
                {
                    //throw new Exception("Unable to decode a non-dictionary godot packet!");
                }

                dic = readDictionary();
            }
            catch (Exception e)
            {
                // Anyone can send garbage, and it used to log a full stack trace for every packet: a flood of them filled the
                // disk. Now a short line, at most a few a minute (Packet Guard bans whoever floods them).
                LogBadPacket(e);
                dic = new Dictionary<string, object>();
            }

            return dic;
        }

        private Object readNext()
        {
            if (++depth > MaxDepth)
                throw new InvalidDataException("Packet nesting is too deep.");

            try
            {
                return readNextInner();
            }
            finally
            {
                depth--;
            }
        }

        private Object readNextInner()
        {
            int v = reader.ReadInt32();

            int type = v & 0xFFFF;
            int flags = v >> 16;

            switch (type)
            {
                case (int)GodotTypes.nullValue:
                    return null;

                case (int)GodotTypes.dictionaryValue:
                    return readDictionary();

                case (int)GodotTypes.arrayValue:
                    return readArray();

                case (int)GodotTypes.stringValue:
                    return readString();

                case (int)GodotTypes.intValue:
                    return readInt(flags);

                case (int)GodotTypes.vector3Value:
                    return readVector3();

                case (int)GodotTypes.quatValue:
                    return readQuat();

                case (int)GodotTypes.boolValue:
                    return readBool();

                case (int)GodotTypes.floatValue:
                    return readFloat(flags);

                case (int)GodotTypes.planeValue:
                    return readPlane();

                case (int)GodotTypes.vector2Value:
                    return readVector2();

                default:
                    LogBadPacket(new InvalidDataException($"unknown value type {type}")); // short and rate limited
                    return new ReadError();
            }
        }

        private Plane readPlane()
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            float dist = reader.ReadSingle();

            return new Plane(x, y, z, dist);
        }

        private double readFloat(int flags)
        {
            if ((flags & 1) == 1)
            {
                return reader.ReadDouble();
            }
            else
            {
                return reader.ReadSingle();
            }
        }

        private bool readBool()
        {
            return reader.ReadInt32() != 0;
        }

        private Quat readQuat()
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            float w = reader.ReadSingle();
            Quat quat = new Quat(x, y, z, w);

            return quat;
        }

        private Vector2 readVector2()
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            Vector2 newVec = new(x, y);

            return newVec;
        }

        private Vector3 readVector3()
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            Vector3 newVec = new(x, y, z);

            return newVec;
        }

        private long readInt(int flags)
        {
            if ((flags & 1) == 1)
            {
                return reader.ReadInt64();
            }
            else
            {
                int v = reader.ReadInt32();
                return v;
            }
        }

        private string readString()
        {
            int stringLength = reader.ReadInt32();

            // a bogus length would otherwise allocate up to ~2GB, or throw on a negative value
            if (stringLength < 0 || stringLength > remainingBytes)
                throw new InvalidDataException($"Invalid string length {stringLength}.");

            char[] stringValue = reader.ReadChars(stringLength);

            // this feild is padded to 4 bytes
            if (4 - ((int)reader.BaseStream.Position % 4) != 4)
            {
                reader.ReadBytes(4 - ((int)reader.BaseStream.Position % 4));
            }

            return new string(stringValue);
        }

        private Dictionary<int, object> readArray()
        {
            Dictionary<int, object> array = new Dictionary<int, object>();

            int elementCount = reader.ReadInt32() & 0x7FFFFFFF;

            // every element takes at least 4 bytes, so a count beyond that is malformed
            if (elementCount > remainingBytes / 4)
                throw new InvalidDataException($"Invalid array length {elementCount}.");

            for (int i = 0; i < elementCount; i++)
            {
                object ins = readNext(); // read the next thing
                array[i] = ins;
            }

            return array;
        }

        private Dictionary<string, object> readDictionary()
        {

            Dictionary<string, object> dic = new Dictionary<string, object>();

            int elementCount = reader.ReadInt32() & 0x7FFFFFFF;

            // a key and a value take at least 8 bytes per entry
            if (elementCount > remainingBytes / 8)
                throw new InvalidDataException($"Invalid dictionary length {elementCount}.");

            for (int i = 0; i < elementCount; i++)
            {
                object keyValue = readNext();
                var key = "NullValue";

                if (keyValue == null || !(keyValue is String)) // if the value is not a string (bad read) break the loop.
                {
                    logger.Error("READ ERROR, KEY PROVIDED IS NOT A STRING!");
                    break; //break from the loop to save the server!
                }
                else
                {
                    key = keyValue.ToString(); // ITS A STRING I SWEAR TO GOD! PLEASEEE
                }

                object value = readNext();

                dic[key] = value;
            }

            return dic;
        }

    }

}