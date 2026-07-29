using Rocket.API;
using Rocket.Core.Logging;
using Rocket.Core.Plugins;
using SDG.Unturned;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;

namespace ForgeStore
{
    /// <summary>
    /// ForgeStore — livraison automatique des achats (RocketMod).
    /// Contrat réel de l'API:
    ///   GET  /api/plugin/queue            -> {meta:{execute_offline}, players:[...]}
    ///   GET  /api/plugin/queue/offline    -> {commands:[{id,command,player:{name,uuid},conditions:{delay}}]}
    ///   GET  /api/plugin/queue/online/{p} -> {commands:[...]} (joueurs listés ET connectés)
    ///   DELETE /api/plugin/queue          -> {"ids":[...]} pour confirmer
    /// Auth: header X-ForgeStore-Secret.
    /// </summary>
    public class ForgeStorePlugin : RocketPlugin<ForgeStoreConfiguration>
    {
        private const string API_BASE = "https://forgestore.net/api/plugin";
        public static ForgeStorePlugin Instance;
        private Coroutine pollCoroutine;

        #region API models
        public class QueueMeta { [JsonProperty("execute_offline")] public bool ExecuteOffline { get; set; } }
        public class QueuePlayer { [JsonProperty("name")] public string Name { get; set; } }
        public class QueueResponse
        {
            [JsonProperty("meta")] public QueueMeta Meta { get; set; }
            [JsonProperty("players")] public List<QueuePlayer> Players { get; set; }
        }
        public class CmdConditions { [JsonProperty("delay")] public int Delay { get; set; } }
        public class CmdPlayer
        {
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("uuid")] public string Uuid { get; set; }
        }
        public class QueueCommand
        {
            [JsonProperty("id")] public int Id { get; set; }
            [JsonProperty("command")] public string Command { get; set; }
            [JsonProperty("conditions")] public CmdConditions Conditions { get; set; }
            [JsonProperty("player")] public CmdPlayer Player { get; set; }
        }
        public class CommandsResponse { [JsonProperty("commands")] public List<QueueCommand> Commands { get; set; } }
        #endregion

        protected override void Load()
        {
            Instance = this;
            if (string.IsNullOrEmpty(Configuration.Instance.SecretKey))
                Logger.Log("[ForgeStore] WARNING: SecretKey not set! Run: forgestore secret YOUR_KEY");
            else
                pollCoroutine = StartCoroutine(PollLoop());
            Logger.Log($"[ForgeStore] Loaded — polling every {Configuration.Instance.PollIntervalSeconds}s");
        }

        protected override void Unload()
        {
            if (pollCoroutine != null) StopCoroutine(pollCoroutine);
            Logger.Log("[ForgeStore] Unloaded.");
        }

        public void RestartPolling()
        {
            if (pollCoroutine != null) StopCoroutine(pollCoroutine);
            pollCoroutine = StartCoroutine(PollLoop());
        }

        private IEnumerator PollLoop()
        {
            while (true)
            {
                yield return StartCoroutine(PollQueue());
                yield return new WaitForSeconds(Mathf.Max(15, Configuration.Instance.PollIntervalSeconds));
            }
        }

        private UnityWebRequest Get(string url)
        {
            var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("X-ForgeStore-Secret", Configuration.Instance.SecretKey);
            req.SetRequestHeader("User-Agent", "ForgeStore-Unturned/1.1.0");
            req.timeout = 8;
            return req;
        }

        public IEnumerator PollQueue()
        {
            if (string.IsNullOrEmpty(Configuration.Instance.SecretKey)) yield break;

            string body;
            using (var req = Get($"{API_BASE}/queue"))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) yield break;
                body = req.downloadHandler.text;
            }

            QueueResponse q = null;
            try { q = JsonConvert.DeserializeObject<QueueResponse>(body); }
            catch (Exception e) { Logger.Log($"[ForgeStore] Parse error: {e.Message}"); }
            if (q == null) yield break;

            // 1. Commandes offline: exécutables immédiatement
            if (q.Meta != null && q.Meta.ExecuteOffline)
                yield return StartCoroutine(FetchAndRun($"{API_BASE}/queue/offline", null));

            // 2. Commandes online: joueurs listés ET connectés
            if (q.Players != null)
                foreach (var p in q.Players)
                {
                    if (string.IsNullOrEmpty(p?.Name)) continue;
                    if (!IsPlayerOnline(p.Name)) continue;
                    yield return StartCoroutine(FetchAndRun(
                        $"{API_BASE}/queue/online/{Uri.EscapeDataString(p.Name)}", p.Name));
                }
        }

        private bool IsPlayerOnline(string name)
        {
            foreach (var client in Provider.clients)
                if (string.Equals(client.playerID.characterName, name, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(client.playerID.playerName,    name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private IEnumerator FetchAndRun(string url, string fallbackName)
        {
            string body;
            using (var req = Get(url))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) yield break;
                body = req.downloadHandler.text;
            }

            CommandsResponse data = null;
            try { data = JsonConvert.DeserializeObject<CommandsResponse>(body); }
            catch { }
            if (data?.Commands == null || data.Commands.Count == 0) yield break;

            Logger.Log($"[ForgeStore] Executing {data.Commands.Count} command(s)");
            var done = new List<int>();
            foreach (var cmd in data.Commands)
            {
                if (string.IsNullOrEmpty(cmd.Command)) continue;
                string pname = cmd.Player?.Name ?? fallbackName ?? "";
                string uuid  = cmd.Player?.Uuid ?? "";
                string command = cmd.Command
                    .Replace("{player}", pname)
                    .Replace("{name}",   pname)
                    .Replace("{uuid}",   uuid);

                int delay = cmd.Conditions?.Delay ?? 0;
                if (delay > 0) yield return new WaitForSeconds(Mathf.Min(delay, 30));

                Commander.execute(CSteamID.Nil, command);
                Logger.Log($"[ForgeStore] Executed: {command}");
                done.Add(cmd.Id);
            }
            yield return StartCoroutine(MarkDone(done));
        }

        private IEnumerator MarkDone(List<int> ids)
        {
            if (ids.Count == 0) yield break;
            string json = JsonConvert.SerializeObject(new { ids });
            byte[] data = Encoding.UTF8.GetBytes(json);
            using (var req = new UnityWebRequest($"{API_BASE}/queue", "DELETE"))
            {
                req.uploadHandler   = new UploadHandlerRaw(data);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("X-ForgeStore-Secret", Configuration.Instance.SecretKey);
                req.timeout = 5;
                yield return req.SendWebRequest();
            }
        }
    }

    public class ForgeStoreConfiguration : IRocketPluginConfiguration
    {
        public string SecretKey           { get; set; } = "";
        public int    PollIntervalSeconds { get; set; } = 30;

        public void LoadDefaults()
        {
            SecretKey           = "";
            PollIntervalSeconds = 30;
        }
    }

    public class ForgeStoreCommand : IRocketCommand
    {
        public string Name => "forgestore";
        public string Help => "ForgeStore plugin commands";
        public string Syntax => "/forgestore <secret|check|info>";
        public AllowedCaller AllowedCaller => AllowedCaller.Console;
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "forgestore.admin" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            if (args.Length == 0) { Logger.Log("Usage: /forgestore <secret|check|info>"); return; }
            switch (args[0].ToLower())
            {
                case "secret":
                    if (args.Length < 2) { Logger.Log("Usage: /forgestore secret <key>"); return; }
                    ForgeStorePlugin.Instance.Configuration.Instance.SecretKey = args[1];
                    ForgeStorePlugin.Instance.Configuration.Save();
                    ForgeStorePlugin.Instance.RestartPolling();
                    Logger.Log("[ForgeStore] Secret key saved — polling restarted.");
                    break;
                case "check":
                    ForgeStorePlugin.Instance.StartCoroutine(ForgeStorePlugin.Instance.PollQueue());
                    Logger.Log("[ForgeStore] Queue check triggered.");
                    break;
                case "info":
                    string key = ForgeStorePlugin.Instance.Configuration.Instance.SecretKey;
                    Logger.Log($"[ForgeStore] Key: {(string.IsNullOrEmpty(key) ? "not set" : "set")}");
                    break;
            }
        }
    }
}