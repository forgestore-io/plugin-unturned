using Rocket.API;
using Rocket.API.Collections;
using Rocket.Core.Logging;
using Rocket.Core.Plugins;
using Rocket.Unturned.Player;
using SDG.Unturned;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;

namespace ForgeStore
{
    public class ForgeStorePlugin : RocketPlugin<ForgeStoreConfiguration>
    {
        private const string API_BASE = "https://forgestore.net/api/plugin";
        public static ForgeStorePlugin Instance;
        private Coroutine pollCoroutine;

        protected override void Load()
        {
            Instance = this;

            if (string.IsNullOrEmpty(Configuration.Instance.SecretKey))
                Logger.Log("[ForgeStore] WARNING: SecretKey not set in configuration!");
            else
            {
                pollCoroutine = StartCoroutine(PollLoop());
                Logger.Log($"[ForgeStore] Plugin loaded. Polling every {Configuration.Instance.PollIntervalSeconds}s");
            }
        }

        protected override void Unload()
        {
            if (pollCoroutine != null) StopCoroutine(pollCoroutine);
            Logger.Log("[ForgeStore] Plugin unloaded.");
        }

        private IEnumerator PollLoop()
        {
            while (true)
            {
                yield return StartCoroutine(PollQueue());
                yield return new WaitForSeconds(Configuration.Instance.PollIntervalSeconds);
            }
        }

        private IEnumerator PollQueue()
        {
            string url = $"{API_BASE}/queue?secret={Configuration.Instance.SecretKey}";
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("User-Agent", "ForgeStore-Unturned/1.0.0");
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success) yield break;
                string body = req.downloadHandler.text;
                if (string.IsNullOrEmpty(body) || body == "[]") yield break;

                var commands = JsonConvert.DeserializeObject<List<QueueItem>>(body);
                if (commands == null || commands.Count == 0) yield break;

                Logger.Log($"[ForgeStore] Executing {commands.Count} command(s)");
                foreach (var cmd in commands)
                {
                    string command = cmd.Command
                        .Replace("{player}", cmd.Player ?? "")
                        .Replace("{name}",   cmd.Player ?? "")
                        .Replace("{uuid}",   cmd.Uuid   ?? "")
                        .Replace("{amount}", cmd.Amount ?? "");

                    Commander.execute(CSteamID.Nil, command);
                    Logger.Log($"[ForgeStore] Executed: {command}");
                    yield return StartCoroutine(MarkDone(cmd.Id));
                }
            }
        }

        private IEnumerator MarkDone(int id)
        {
            string json = JsonConvert.SerializeObject(new { id, secret = Configuration.Instance.SecretKey });
            byte[] data = Encoding.UTF8.GetBytes(json);
            using (UnityWebRequest req = new UnityWebRequest($"{API_BASE}/queue", "DELETE"))
            {
                req.uploadHandler   = new UploadHandlerRaw(data);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                yield return req.SendWebRequest();
            }
        }

        public class QueueItem
        {
            [JsonProperty("id")]      public int    Id      { get; set; }
            [JsonProperty("command")] public string Command { get; set; }
            [JsonProperty("player")]  public string Player  { get; set; }
            [JsonProperty("uuid")]    public string Uuid    { get; set; }
            [JsonProperty("amount")]  public string Amount  { get; set; }
        }
    }

    public class ForgeStoreConfiguration : IRocketPluginConfiguration
    {
        public string SecretKey          { get; set; } = "";
        public int    PollIntervalSeconds { get; set; } = 30;

        public void LoadDefaults()
        {
            SecretKey           = "";
            PollIntervalSeconds = 30;
        }
    }

    public class ForgeStoreCommand : IRocketCommand
    {
        public string Name        => "forgestore";
        public string Help        => "ForgeStore plugin commands";
        public string Syntax      => "/forgestore <secret|check|info>";
        public AllowedCaller AllowedCaller => AllowedCaller.Console;
        public List<string> Aliases        => new List<string>();
        public List<string> Permissions    => new List<string> { "forgestore.admin" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            if (args.Length == 0) { Logger.Log("Usage: /forgestore <secret|check|info>"); return; }
            switch (args[0].ToLower())
            {
                case "secret":
                    if (args.Length < 2) { Logger.Log("Usage: /forgestore secret <key>"); return; }
                    ForgeStorePlugin.Instance.Configuration.Instance.SecretKey = args[1];
                    ForgeStorePlugin.Instance.Configuration.Save();
                    Logger.Log("[ForgeStore] Secret key saved!");
                    break;
                case "check":
                    ForgeStorePlugin.Instance.StartCoroutine("PollQueue");
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
