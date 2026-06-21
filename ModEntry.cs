using HarmonyLib;
using Microsoft.Xna.Framework.Audio;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Characters;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Pelican_XVASynth
{
    /// <summary>The mod entry point.</summary>
    public partial class ModEntry : Mod
    {
        public static IMonitor SMonitor;
        public static IModHelper SHelper;
        public static ModConfig Config;

        public static ModEntry context;

        public static Harmony harmony;
        public static GameVoices gameVoices = new GameVoices();
        public static readonly string xVaSynthPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xVASynth", "realTimeTTS");
        public static SoundEffect voiceSound;
        public static SoundEffectInstance activeVoiceInstance; // Track active audio playback
        public static int currentRequestId = 0; // Track active request session
        public static Dictionary<string, GameVoice> voiceDict = new Dictionary<string, GameVoice>();

        /// <summary>The mod entry point, called after the mod is first loaded.</summary>
        /// <param name="helper">Provides simplified APIs for writing mods.</param>
        public override void Entry(IModHelper helper)
        {
            Config = Helper.ReadConfig<ModConfig>();

            if (!Config.EnableMod)
                return;

            context = this;

            SMonitor = Monitor;
            SHelper = helper;

            Helper.Events.GameLoop.OneSecondUpdateTicked += GameLoop_OneSecondUpdateTicked;

            harmony = new Harmony(ModManifest.UniqueID);

            harmony.Patch(
               original: AccessTools.Constructor(typeof(DialogueBox), new Type[] { typeof(Dialogue) }),
               postfix: new HarmonyMethod(typeof(ModEntry), nameof(ModEntry.DialogueBox_Ctor_Postfix))
            );

            // Added Prefix to intercept skips before dialogue advances or closes
            harmony.Patch(
               original: AccessTools.Method(typeof(DialogueBox), nameof(DialogueBox.receiveLeftClick)),
               prefix: new HarmonyMethod(typeof(ModEntry), nameof(ModEntry.DialogueBox_receiveLeftClick_Prefix)),
               postfix: new HarmonyMethod(typeof(ModEntry), nameof(ModEntry.DialogueBox_receiveLeftClick_Postfix))
            );
        }

        private void GameLoop_OneSecondUpdateTicked(object sender, OneSecondUpdateTickedEventArgs e)
        {
            LoadGameVoices();

            var configMenu = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (configMenu != null)
            {
                configMenu.Register(
                    mod: ModManifest,
                    reset: () => Config = new ModConfig(),
                    save: () => Helper.WriteConfig(Config)
                );
                configMenu.AddBoolOption(
                    mod: ModManifest,
                    name: () => "Mod Enabled",
                    getValue: () => Config.EnableMod,
                    setValue: value => Config.EnableMod = value
                );
                configMenu.AddSectionTitle(
                    mod: ModManifest,
                    text: () => "Voices"
                );
                var voiceStrings = new Dictionary<string, string>();
                voiceStrings.Add("", "none");
                foreach (var kvp in gameVoices.games)
                {
                    foreach (var v in kvp.Value)
                    {
                        voiceStrings[kvp.Key + ":" + v.id] = $"{v.name} ({kvp.Key})";
                    }
                }
                Monitor.Log($"list of {voiceStrings.Count} game voices");

                configMenu.AddNumberOption(
                    mod: ModManifest,
                    name: () => "Max Seconds Wait ",
                    tooltip: () => "Cancel speech synthesis if it takes longer than this",
                    getValue: () => Config.MaxSecondsWait,
                    setValue: value => Config.MaxSecondsWait = value,
                    min: 1,
                    max: 60
                );
                configMenu.AddNumberOption(
                    mod: ModManifest,
                    name: () => "Milliseconds To Prepare",
                    tooltip: () => "Wait this many milliseconds for the engine to generate the voice before synthesizing",
                    getValue: () => Config.MillisecondsPrepare,
                    setValue: value => Config.MillisecondsPrepare = value
                );
                configMenu.AddNumberOption(
                    mod: ModManifest,
                    name: () => "Max Letters To Prepare",
                    tooltip: () => "Only pause to prepare if the number of letters in the string is equal to or smaller than this.",
                    getValue: () => Config.MaxLettersToPrepare,
                    setValue: value => Config.MaxLettersToPrepare = value
                );

                foreach (var kvp in Helper.GameContent.Load<Dictionary<string, CharacterData>>("Data\\Characters"))
                {
                    configMenu.AddTextOption(
                        mod: ModManifest,
                        name: () => kvp.Key,
                        getValue: () => voiceDict.ContainsKey(kvp.Key) ? voiceDict[kvp.Key].game + ":" + voiceDict[kvp.Key].id : "",
                        setValue: delegate (string value) {
                            var parts = value.Split(':');
                            if (parts.Length != 2)
                            {
                                voiceDict.Remove(kvp.Key);
                                return;
                            }
                            voiceDict[kvp.Key] = new GameVoice(parts[0], parts[1]);
                            SaveGameVoices();
                        },
                        allowedValues: voiceStrings.Keys.ToArray(),
                        formatAllowedValue: delegate (string value) { return voiceStrings[value]; }
                    );
                }
            }

            Helper.Events.GameLoop.OneSecondUpdateTicked -= GameLoop_OneSecondUpdateTicked;
        }

        private void SaveGameVoices()
        {
            List<string> output = new List<string>();
            foreach (var kvp in voiceDict)
            {
                output.Add($"{kvp.Key}:{kvp.Value.game}:{kvp.Value.id}");
            }
            Config.NPCGameVoices = string.Join(",", output);
        }

        private void LoadGameVoices()
        {
            string voicesPath = Path.Combine(xVaSynthPath, "xVASynthVoices.json");
            if (!File.Exists(voicesPath))
            {
                SMonitor.Log($"Voices file not found at {voicesPath}");
                return;
            }

            using (StreamReader reader = File.OpenText(voicesPath))
            {
                JsonSerializer serializer = new JsonSerializer();
                gameVoices = (GameVoices)serializer.Deserialize(reader, typeof(GameVoices));
                int count = 0;
                foreach (var kvp in gameVoices.games)
                {
                    count += kvp.Value.Count;
                    Monitor.Log($"Loaded {kvp.Value.Count} voices for {kvp.Key}");
                }
                Monitor.Log($"Loaded {count} voices for {gameVoices.games.Count} games", LogLevel.Debug);
            }
            foreach (var ss in Config.NPCGameVoices.Split(','))
            {
                var ngv = ss.Split(':');
                if (ngv.Length != 3)
                    continue;
                voiceDict[ngv[0]] = new GameVoice(ngv[1], ngv[2]);
            }
        }

        public static void DialogueBox_Ctor_Postfix(Dialogue dialogue)
        {
            PlayDialogue(dialogue);
        }

        public static void DialogueBox_receiveLeftClick_Prefix(DialogueBox __instance)
        {
            if (__instance.transitioning || __instance.characterDialogue == null)
                return;

            // Use SMAPI Reflection to safely access the private 'characterIndex' field
            int characterIndex = SHelper.Reflection.GetField<int>(__instance, "characterIndex").GetValue();

            // If characterIndex has reached or passed the text length, it's fully displayed.
            // Clicking now will advance the page or close the dialogue box entirely.
            if (__instance.currentDialogueString != null && characterIndex >= __instance.currentDialogueString.Length)
            {
                CancelCurrentVoice();
            }
        }

        public static void DialogueBox_receiveLeftClick_Postfix(DialogueBox __instance)
        {
            if (!__instance.transitioning && __instance.characterDialogue != null)
                PlayDialogue(__instance.characterDialogue);
        }

        public static void CancelCurrentVoice()
        {
            currentRequestId++; // Invalidates any older running CheckForWav loops
            currentDialogue = "";

            if (activeVoiceInstance != null)
            {
                try
                {
                    if (activeVoiceInstance.State == SoundState.Playing)
                    {
                        activeVoiceInstance.Stop();
                    }
                    activeVoiceInstance.Dispose();
                }
                catch (Exception) { }
                activeVoiceInstance = null;
            }
        }

        public static string currentDialogue = "";

        public static void PlayDialogue(Dialogue dialogue)
        {
            if (!Config.EnableMod || dialogue.speaker == null || dialogue.dialogues[dialogue.currentDialogueIndex].Text == currentDialogue)
                return;
            PlayDialogue(dialogue.speaker.Name, dialogue.dialogues[dialogue.currentDialogueIndex].Text);
        }

        public static void PlayDialogue(string name, string dialogue)
        {
            if (!voiceDict.ContainsKey(name))
            {
                SMonitor.Log($"No game voice set for {name}");
                return;
            }
            GameVoice voice = voiceDict[name];
            if (!gameVoices.games.ContainsKey(voice.game))
            {
                SMonitor.Log($"Game {voice.game} not found for {name}", LogLevel.Warn);
            }
            if (!gameVoices.games[voice.game].Exists(v => v.id == voice.id))
            {
                SMonitor.Log($"Voice {voice.id} for game {voice.game} not found for {name}", LogLevel.Warn);
            }
            SendToXVASynth(voice, dialogue);
        }

        private static async void SendToXVASynth(GameVoice voice, string dialogue)
        {
            // Cancel any active line before submitting a new request
            CancelCurrentVoice();

            int requestId = currentRequestId;
            currentDialogue = dialogue;
            SMonitor.Log($"Sending speech {dialogue} for voice {voice.id}, game {voice.game} to xVASynth (Req ID: {requestId})");

            GameVoiceText text = new GameVoiceText()
            {
                gameId = voice.game,
                voiceId = voice.id,
                vol = 1f,
                text = ""
            };

            if (File.Exists(Path.Combine(xVaSynthPath, "output.wav")))
            {
                try { File.Delete(Path.Combine(xVaSynthPath, "output.wav")); } catch { }
            }

            string speechPath = Path.Combine(xVaSynthPath, "xVASynthText.json");
            using (StreamWriter file = File.CreateText(speechPath))
            {
                JsonSerializer serializer = new JsonSerializer();
                serializer.Serialize(file, text);
            }
            if (dialogue == null || dialogue.Length == 0)
                return;

            if (dialogue.Length <= Config.MaxLettersToPrepare && Config.MillisecondsPrepare > 0)
            {
                await Task.Delay(Config.MillisecondsPrepare);
            }

            // Double check if canceled during the delay
            if (requestId != currentRequestId)
                return;

            text.text = dialogue;
            using (StreamWriter file = File.CreateText(speechPath))
            {
                JsonSerializer serializer = new JsonSerializer();
                serializer.Serialize(file, text);
            }

            CheckForWav(requestId, 0);
        }

        public static async void CheckForWav(int requestId, int currentTicks)
        {
            string wavPath = Path.Combine(xVaSynthPath, "output.wav");
            if (File.Exists(wavPath))
            {
                // If the dialogue was skipped/changed while this file was generating,
                // delete it immediately so it doesn't clutter or conflict with newer requests.
                if (requestId != currentRequestId)
                {
                    try { File.Delete(wavPath); } catch { }
                    return;
                }

                SMonitor.Log($"Playing output.wav file");
                try
                {
                    using (FileStream fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read))
                    {
                        voiceSound = SoundEffect.FromStream(fs);
                    }

                    if (requestId != currentRequestId)
                    {
                        try { File.Delete(wavPath); } catch { }
                        return;
                    }

                    activeVoiceInstance = voiceSound.CreateInstance();
                    activeVoiceInstance.Play();
                }
                catch (Exception e)
                {
                    SMonitor.Log($"Error processing audio: {e.Message}", LogLevel.Error);
                }

                try { File.Delete(wavPath); } catch { }
                return;
            }

            await Task.Delay(100);
            currentTicks++;

            if (currentTicks / 10f > Config.MaxSecondsWait)
            {
                if (requestId == currentRequestId)
                {
                    SMonitor.Log($"Timeout waiting for output.wav file");
                    currentDialogue = "";
                }
                return;
            }

            CheckForWav(requestId, currentTicks);
        }
    }
}
