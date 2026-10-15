using System.Globalization;
using System.Text.RegularExpressions;
using Cove.GodotFormat;

namespace Cove.Server.Utils
{
    internal class WorldFile
    {

        // for reading the point positions from a .tscn file (main_zone.tscn)
        public static List<Vector3> readPoints(string nodeGroup, string file)
        {
            List<Vector3> points = new List<Vector3>();

            // split the file into lines
            string[] lines = file.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {

                Match isFishPoint = Regex.Match(lines[i], @"groups=\[([^\]]*)\]");
                if (isFishPoint.Success && isFishPoint.Groups[1].Value == $"\"{nodeGroup}\"")
                {
                    // the transform is on the line after the node, so there has to be one
                    if (i + 1 >= lines.Length)
                        continue;

                    string transformPattern = @"Transform\(.*?,\s*(-?\d+\.?\d*),\s*(-?\d+\.?\d*),\s*(-?\d+\.?\d*)\s*\)";
                    Match match = Regex.Match(lines[i + 1], transformPattern);

                    // a node without a readable transform used to throw on float.Parse("") and abort startup
                    if (!match.Success)
                        continue;

                    if (!float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                        || !float.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                        || !float.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                        continue;

                    points.Add(new Vector3(x, y, z));
                }
            }

            return points;
        }

    }
}
