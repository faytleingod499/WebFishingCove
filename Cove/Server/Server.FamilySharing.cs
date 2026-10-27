using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Cove.Server.Actor;
using Cove.Server.Utils;
using Steamworks;

namespace Cove.Server
{
    /// <summary>
    /// Keeps Steam Family Sharing accounts (a borrowed copy of WEBFISHING) out of the server.
    ///
    /// How: the game never sends Steam an auth ticket, so the usual Steamworks owner check is not possible. Instead,
    /// when somebody asks to join the lobby, the server asks Steam's web API (IPlayerService/IsPlayingSharedGame)
    /// whether that player is playing a copy borrowed from somebody else. The question is asked in the background,
    /// and the player's game keeps asking to join about once a second, so the answer is used on the next ask.
    ///
    /// Settings live in familysharing.txt next to the server (created on first start; it needs a Steam Web API key).
    /// Admins and Steam IDs on the allow list are never checked. If Steam can't be asked (no key, network or Steam
    /// trouble) everybody is let in, and the reason is written to packetguard.log.
    /// </summary>
    public partial class CoveServer
    {
        private const string FamilyShareFileName = "familysharing.txt";
        private static readonly string FamilyShareFilePath = $"{AppDomain.CurrentDomain.BaseDirectory}{FamilyShareFileName}";

        // an answer is remembered for a while so the repeated join requests don't each ask Steam again
        private const long ShareKeepNotSharedMs = 30 * 60 * 1000; // a normal player
        private const long ShareKeepSharedMs = 2 * 60 * 1000;      // a borrowed copy (asked again after a couple of minutes)
        private const long ShareKeepFailedMs = 30 * 1000;          // Steam couldn't be asked; try again shortly

        private static readonly HttpClient ShareHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

        private enum ShareVerdict
        {
            Allow,
            Deny,
            Wait,
        }

        private sealed class ShareAnswer
        {
            public ulong Lender;      // 0 = the player owns the game (or Steam could not say)
            public bool Failed;       // Steam could not be asked, so the player is let in
            public long ExpiresAtMs;  // Environment.TickCount64 when this answer is forgotten
        }

        private readonly ConcurrentDictionary<ulong, ShareAnswer> shareAnswers = new();
        private readonly ConcurrentDictionary<ulong, byte> shareLookups = new();
        private readonly object shareSettingsLock = new();

        private bool shareBlockEnabled = true;
        private string shareApiKey = "";
        private HashSet<ulong> shareAllowed = new();
        private DateTime shareSettingsWriteTime = DateTime.MinValue;
        private long shareLastSettingsCheckMs;
        private long shareLastWarnMs;

        /// <summary>Reads familysharing.txt (creating it the first time). Called at startup and again when the file changes.</summary>
        private void LoadFamilyShareSettings()
        {
            lock (shareSettingsLock)
            {
                try
                {
                    if (!File.Exists(FamilyShareFilePath))
                        File.WriteAllText(FamilyShareFilePath, FamilyShareDefaultFile(), new UTF8Encoding(false));

                    shareSettingsWriteTime = File.GetLastWriteTimeUtc(FamilyShareFilePath);
                    Dictionary<string, string> values = ConfigReader.ReadFile(File.ReadAllText(FamilyShareFilePath));

                    // only an explicit "false" turns it off, so a typo can't switch the protection off by accident
                    shareBlockEnabled = !values.TryGetValue("blockFamilySharing", out string? enabled)
                        || !enabled.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
                    shareApiKey = values.TryGetValue("steamApiKey", out string? key) ? key.Trim() : "";

                    HashSet<ulong> allowed = new();
                    if (values.TryGetValue("allow", out string? list))
                    {
                        foreach (string part in list.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (ulong.TryParse(part, out ulong id))
                                allowed.Add(id);
                            else
                                Log($"familysharing.txt: \"{part}\" in allow is not a Steam ID, ignored.");
                        }
                    }
                    shareAllowed = allowed;

                    // old answers were made under old settings
                    shareAnswers.Clear();

                    if (!shareBlockEnabled)
                        Log("Family sharing block: OFF (blockFamilySharing = false in familysharing.txt).");
                    else if (shareApiKey.Length == 0)
                        Log("Family sharing block: ON, but steamApiKey in familysharing.txt is empty, so nobody is checked yet.");
                    else
                        Log($"Family sharing block: ON ({shareAllowed.Count} Steam IDs on the allow list).");
                }
                catch (Exception e)
                {
                    Log($"Could not read {FamilyShareFileName}, family sharing is not checked: {e.Message}");
                    shareBlockEnabled = false;
                }
            }
        }

        /// <summary>Picks up edits to familysharing.txt without a restart (looked at most every few seconds).</summary>
        private void ReloadFamilyShareSettingsIfChanged()
        {
            long now = Environment.TickCount64;
            if (now - shareLastSettingsCheckMs < 3000)
                return;
            shareLastSettingsCheckMs = now;

            try
            {
                if (File.Exists(FamilyShareFilePath) && File.GetLastWriteTimeUtc(FamilyShareFilePath) != shareSettingsWriteTime)
                    LoadFamilyShareSettings();
            }
            catch
            {
                // a file that can't be looked at keeps the settings as they were
            }
        }

        /// <summary>
        /// Decides about a lobby join request. Deny = refuse (a borrowed copy), Wait = Steam is being asked,
        /// say nothing yet (the game asks again in about a second), Allow = carry on as usual.
        /// </summary>
        private ShareVerdict CheckFamilySharing(CSteamID userId)
        {
            ReloadFamilyShareSettingsIfChanged();

            if (!shareBlockEnabled || shareApiKey.Length == 0)
                return ShareVerdict.Allow;

            ulong id = userId.m_SteamID;
            if (shareAllowed.Contains(id) || isPlayerAdmin(userId))
                return ShareVerdict.Allow;

            long now = Environment.TickCount64;
            if (shareAnswers.TryGetValue(id, out ShareAnswer? known) && known.ExpiresAtMs > now)
                return ShareVerdictFor(userId, known);

            // not known yet: ask Steam in the background, once
            if (shareLookups.TryAdd(id, 0))
            {
                string key = shareApiKey;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        shareAnswers[id] = await AskSteamAboutSharing(id, key);
                    }
                    catch (Exception e)
                    {
                        shareAnswers[id] = new ShareAnswer
                        {
                            Failed = true,
                            ExpiresAtMs = Environment.TickCount64 + ShareKeepFailedMs,
                        };
                        ShareWarn($"Family sharing check for {id} failed, letting them in: {ShareScrub(e.Message, key)}");
                    }
                    finally
                    {
                        shareLookups.TryRemove(id, out _);
                    }
                });
            }

            return ShareVerdict.Wait;
        }

        private ShareVerdict ShareVerdictFor(CSteamID userId, ShareAnswer answer)
        {
            if (answer.Failed || answer.Lender == 0)
                return ShareVerdict.Allow;

            string name = GuardSafe(GuardNameOf(userId), 40);
            GuardNote(userId.m_SteamID, "familyshare",
                $"FAMILYSHARE {userId.m_SteamID} ({name}): join refused, the game is borrowed from {answer.Lender}");

            // the admins who are online hear about it in game (once per player every 10 seconds, like the log line)
            if (ShareNoteDue(userId.m_SteamID))
            {
                foreach (WFPlayer admin in AllPlayers.ToList())
                {
                    if (isPlayerAdmin(admin.SteamId))
                        messagePlayer($"Refused {name} ({userId.m_SteamID}): family sharing account (game borrowed from {answer.Lender}).", admin.SteamId, "ff8080");
                }
            }
            return ShareVerdict.Deny;
        }

        private readonly Dictionary<ulong, long> shareAdminNoteTimes = new();

        private bool ShareNoteDue(ulong id)
        {
            long now = Environment.TickCount64;
            lock (shareAdminNoteTimes)
            {
                if (shareAdminNoteTimes.TryGetValue(id, out long last) && now - last < 10_000)
                    return false;
                shareAdminNoteTimes[id] = now;
                if (shareAdminNoteTimes.Count > 2000)
                    shareAdminNoteTimes.Clear();
                return true;
            }
        }

        private async Task<ShareAnswer> AskSteamAboutSharing(ulong id, string key)
        {
            uint appId = 3146520; // WEBFISHING
            try
            {
                uint running = SteamUtils.GetAppID().m_AppId;
                if (running != 0)
                    appId = running;
            }
            catch
            {
                // keep the default
            }

            string url = $"https://api.steampowered.com/IPlayerService/IsPlayingSharedGame/v1/?key={Uri.EscapeDataString(key)}&steamid={id}&appid_playing={appId}";
            using HttpResponseMessage response = await ShareHttp.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Steam answered {(int)response.StatusCode} {response.ReasonPhrase} (is steamApiKey in {FamilyShareFileName} right?)");

            string body = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("response", out JsonElement inner)
                || !inner.TryGetProperty("lender_steamid", out JsonElement lenderElement))
                throw new Exception("Steam's answer had no lender_steamid");

            ulong lender = 0;
            if (lenderElement.ValueKind == JsonValueKind.String)
                ulong.TryParse(lenderElement.GetString(), out lender);
            else if (lenderElement.ValueKind == JsonValueKind.Number)
                lenderElement.TryGetUInt64(out lender);

            GuardLog($"SHARECHECK {id} ({GuardSafe(GuardNameOf(new CSteamID(id)), 40)}): {(lender == 0 ? "owns the game" : "borrowed from " + lender)}");

            return new ShareAnswer
            {
                Lender = lender,
                ExpiresAtMs = Environment.TickCount64 + (lender == 0 ? ShareKeepNotSharedMs : ShareKeepSharedMs),
            };
        }

        // one warning per 30 seconds, so a Steam outage can't fill the log
        private void ShareWarn(string text)
        {
            long now = Environment.TickCount64;
            if (now - shareLastWarnMs < 30_000)
                return;
            shareLastWarnMs = now;
            logger.Warning(text);
            GuardLog("FAMILYSHARE " + text);
        }

        // never let the API key reach a log through an error message
        private static string ShareScrub(string text, string key)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key))
                return text;
            return text.Replace(key, "****").Replace(Uri.EscapeDataString(key), "****");
        }

        private static string FamilyShareDefaultFile()
        {
            string[] lines =
            {
                "# ======================================================================",
                "#  Family sharing block",
                "# ======================================================================",
                "#",
                "#  Keeps players out whose copy of WEBFISHING is borrowed through Steam",
                "#  Family Sharing. They are refused at the lobby (not written to bans.txt),",
                "#  and the admins who are online are told in game. Every check, and every",
                "#  refusal, is written to packetguard.log (SHARECHECK / FAMILYSHARE lines).",
                "#",
                "#  It needs a free Steam Web API key:",
                "#    1. Sign in at https://steamcommunity.com/dev/apikey",
                "#    2. Type any domain name (for example localhost) and register the key",
                "#    3. Paste the key after steamApiKey below. Keep it private.",
                "#  Changes to this file are picked up within a few seconds, no restart needed.",
                "#",
                "#  Admins and the Steam IDs in allow are never checked. If Steam can't be",
                "#  asked (no key, Steam or network trouble) everybody is let in.",
                "# ======================================================================",
                "",
                "# true = refuse family sharing accounts, false = let them in",
                "blockFamilySharing = true",
                "",
                "# your Steam Web API key",
                "steamApiKey = ",
                "",
                "# Steam IDs (17 digits) that may join even on a borrowed copy, separated by commas",
                "allow = ",
                "",
            };
            return string.Join(Environment.NewLine, lines);
        }
    }
}
