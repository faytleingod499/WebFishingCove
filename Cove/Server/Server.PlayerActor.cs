using Cove.Server.Actor;
using Steamworks;

namespace Cove.Server
{
    public partial class CoveServer
    {
        // A player's game sends "instance_actor" for its own character right after it joins. If that packet arrives before the
        // lobby join was accepted (the player is not in AllPlayers yet), it used to be dropped: the character was never
        // registered, the join never finished (no welcome message), and every action the player sent afterwards looked like an
        // action for a character that doesn't exist, which Packet Guard bans as a queue flood. It is kept here instead and
        // registered as soon as the player is in the list.
        private readonly Dictionary<ulong, (long actorId, long tick)> pendingPlayerActors = new();
        private const long PendingPlayerActorMs = 30_000;

        /// <summary>Registers the player's own character. False when the player is not in AllPlayers yet.</summary>
        private bool RegisterPlayerActor(CSteamID sender, long actorID)
        {
            WFPlayer? thisPlayer = AllPlayers.Find(p => p.SteamId.m_SteamID == sender.m_SteamID);
            if (thisPlayer == null)
                return false;

            thisPlayer.InstanceID = actorID;

            // a client can send this more than once, don't track (or announce) the same player twice
            bool alreadyTracked = allActors.Contains(thisPlayer);
            if (!alreadyTracked)
                allActors.Add(thisPlayer); // add the player to the actor list

            if (!alreadyTracked)
            {
                // print out a join message
                Log($"[{thisPlayer.FisherID}] {thisPlayer.Username} joined. [{AllPlayers.Count}/{MaxPlayers}]");
                GuardLog($"JOINED    {thisPlayer.SteamId.m_SteamID} ({GuardSafe(thisPlayer.Username, 40)}): game finished joining [{AllPlayers.Count}/{MaxPlayers}]");
                sendWebLobbyPacket(thisPlayer.SteamId);
                updatePlayercount();

                foreach (var p in loadedPlugins.ToList())
                    SafePluginCall(p, () => p.plugin.onPlayerJoin(thisPlayer));
            }
            return true;
        }

        /// <summary>Keeps a player character packet that came before the player was in the list.</summary>
        private void KeepPendingPlayerActor(CSteamID sender, long actorID)
        {
            lock (pendingPlayerActors)
            {
                if (pendingPlayerActors.Count > 500)
                    pendingPlayerActors.Clear();
                pendingPlayerActors[sender.m_SteamID] = (actorID, Environment.TickCount64);
            }
            Log("No fisher found for player instance yet, keeping it until the join is accepted.");
            GuardNote(sender.m_SteamID, "pending", $"PENDING   {sender.m_SteamID} ({GuardSafe(GuardNameOf(sender), 40)}): character packet arrived before the join was accepted, kept for registering");
        }

        /// <summary>Registers a kept character packet once the player is in the list (checked on every packet from that player).</summary>
        private void ApplyPendingPlayerActor(CSteamID sender)
        {
            if (pendingPlayerActors.Count == 0)
                return; // the usual case, no lock needed
            (long actorId, long tick) pending;
            lock (pendingPlayerActors)
            {
                if (pendingPlayerActors.Count == 0 || !pendingPlayerActors.TryGetValue(sender.m_SteamID, out pending))
                    return;
                if (Environment.TickCount64 - pending.tick > PendingPlayerActorMs)
                {
                    pendingPlayerActors.Remove(sender.m_SteamID);
                    return;
                }
                if (!AllPlayers.Any(p => p.SteamId.m_SteamID == sender.m_SteamID))
                    return; // not accepted yet, try again with the next packet
                pendingPlayerActors.Remove(sender.m_SteamID);
            }
            if (RegisterPlayerActor(sender, pending.actorId))
                GuardLog($"LATE      {sender.m_SteamID} ({GuardSafe(GuardNameOf(sender), 40)}): character registered after the join was accepted");
        }
    }
}
