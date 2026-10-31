using System;
using System.Collections.Generic;
using System.Linq;

namespace Cove.Server
{
    public partial class CoveServer
    {
        // While a remote command runs (on the calling thread only), anything the command says to the host is handed to
        // this function instead of only going into the console. Used by the Discord bridge to answer in the channel.
        [ThreadStatic]
        private static Action<string>? remoteReplySink;

        /// <summary>
        /// Runs a server command as the host, for a trusted remote admin (the Discord bridge). The caller must already
        /// have checked WHO is allowed to do this. Commands that only the host may use are refused unless they are on
        /// allowHostCommands. Whatever the command says to the host goes to reply. Returns false if nothing ran.
        /// </summary>
        public bool RunRemoteCommand(string command, string[] args, Action<string> reply, ISet<string> blocked)
        {
            RegisteredCommand? cmd = FindCommand(command);
            if (cmd == null)
            {
                reply($"Unknown command: {command}");
                return false;
            }

            if (blocked.Contains(cmd.Command) || cmd.Aliases.Any(a => blocked.Contains(a)) || blocked.Contains(command.ToLowerInvariant()))
            {
                reply($"{cmd.Command} is not allowed from Discord.");
                return false;
            }

            // same lock the network thread holds while it handles a chat command, so the command never races a packet
            lock (stateLock)
            {
                Action<string>? previous = remoteReplySink;
                remoteReplySink = reply;
                try
                {
                    InvokeCommand(serverPlayer, cmd.Command, args);
                }
                catch (Exception e)
                {
                    logger.Error($"Remote command \"{cmd.Command}\" threw an exception:");
                    logger.Error(e.ToString());
                    reply("That command failed, check the arguments and try again.");
                    return false;
                }
                finally
                {
                    remoteReplySink = previous;
                }
            }

            return true;
        }
    }
}
