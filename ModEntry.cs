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
        public static SoundEffectInstance activeVoiceInstance;
        public static int currentRequestId = 0;
        private static int beforeClickDialogueIndex = -1;

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
                voiceStrings.Add("none:none", "none");
                foreach (var kvp in gameVoices.games)
                {
                    foreach(var v in kvp.Value)
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

                bool modifiedConfig = false;
                var characters = Helper.GameContent.Load<Dictionary<string, CharacterData>>("Data\\Characters");
                foreach (var characterName in characters.Keys)
                {
                    if (!Config.Voices.ContainsKey(characterName))
                    {
                        Config.Voices[characterName] = new VoiceSetup { Game = "none", Voice = "none", Pitch = 0.0f };
                        modifiedConfig = true;
                    }
                }

                if (modifiedConfig)
                {
                    Helper.WriteConfig(Config);
                }
                
                foreach (var kvp in characters)
                {
                    configMenu.AddTextOption(
                        mod: ModManifest,
                        name: () => $"{kvp.Key} Voice",
                        getValue: () => {
                            if (Config.Voices.ContainsKey(kvp.Key))
                            {
                                string keyVal = Config.Voices[kvp.Key].Game + ":" + Config.Voices[kvp.Key].Voice;
                                return voiceStrings.ContainsKey(keyVal) ? keyVal : "none:none";
                            }
                            return "none:none";
                        },
                        setValue: delegate (string value) {
                            var parts = value.Split(':');
                            if (!Config.Voices.ContainsKey(kvp.Key))
                                Config.Voices[kvp.Key] = new VoiceSetup();

                            if (parts.Length != 2 || (parts[0] == "none" && parts[1] == "none"))
                            {
                                Config.Voices[kvp.Key].Game = "none";
                                Config.Voices[kvp.Key].Voice = "none";
                            }
                            else
                            {
                                Config.Voices[kvp.Key].Game = parts[0];
                                Config.Voices[kvp.Key].Voice = parts[1];
                            }
                            Helper.WriteConfig(Config);
                        },
                        allowedValues: voiceStrings.Keys.ToArray(),
                        formatAllowedValue: delegate (string value) { return voiceStrings.ContainsKey(value) ? voiceStrings[value] : "none"; }
                    );

                    // Fixed: GMCM handles integer sliders, converted to/from float config variables
                    configMenu.AddNumberOption(
                        mod: ModManifest,
                        name: () => $"{kvp.Key} Pitch",
                        tooltip: () => $"Alter the vocal pitch frequency for {kvp.Key} (-100 lowest to 100 highest)",
                        getValue: () => {
                            if (Config.Voices.ContainsKey(kvp.Key))
                            {
                                // Multiply by 100 and cast to int for GMCM display
                                return (int)(Config.Voices[kvp.Key].Pitch * 100f);
                            }
                            return 0;
                        },
                        setValue: (int value) => {
                            if (!Config.Voices.ContainsKey(kvp.Key))
                                Config.Voices[kvp.Key] = new VoiceSetup { Game = "none", Voice = "none" };

                            // Divide by 100.0f to store as a clean decimal float in config
                            Config.Voices[kvp.Key].Pitch = value / 100f;
                            Helper.WriteConfig(Config);
                        },
                        min: -100,
                        max: 100,
                        interval: 5
                    );
                }
            }

            Helper.Events.GameLoop.OneSecondUpdateTicked -= GameLoop_OneSecondUpdateTicked;
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
        }

        public static void DialogueBox_Ctor_Postfix(Dialogue dialogue)
        {
            PlayDialogue(dialogue);
        }

        public static void DialogueBox_receiveLeftClick_Prefix(DialogueBox __instance)
        {
            if (__instance.transitioning || __instance.characterDialogue == null)
                return;

            beforeClickDialogueIndex = __instance.characterDialogue.currentDialogueIndex;
        }

        public static void DialogueBox_receiveLeftClick_Postfix(DialogueBox __instance)
        {
            if (__instance.characterDialogue == null)
            {
                CancelCurrentVoice();
                return;
            }

            if (__instance.characterDialogue.currentDialogueIndex != beforeClickDialogueIndex)
            {
                CancelCurrentVoice();
                
                if (!__instance.transitioning)
                    PlayDialogue(__instance.characterDialogue);
            }
        }

        public static void CancelCurrentVoice()
        {
            currentRequestId++;
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

        public static void PlayDialogue(string name, string dialogue) {
            if (!Config.Voices.ContainsKey(name))
            {
                return;
            }
            
            VoiceSetup voice = Config.Voices[name];
            
            if (voice.Game == "none" || voice.Voice == "none")
            {
                return;
            }

            if (!gameVoices.games.ContainsKey(voice.Game))
            {
                SMonitor.Log($"Voice profiles for game '{voice.Game}' (assigned to {name}) are missing from xVASynth. Skipping synthesis.", LogLevel.Warn);
                return;
            }
            
            if (!gameVoices.games[voice.Game].Exists(v => v.id == voice.Voice))
            {
                SMonitor.Log($"Voice model ID '{voice.Voice}' (assigned to {name}) is missing from xVASynth. Skipping synthesis.", LogLevel.Warn);
                return;
            }
            
            SendToXVASynth(voice, dialogue);
        }

        private static async void SendToXVASynth(VoiceSetup voice, string dialogue)
        {
            CancelCurrentVoice();

            int requestId = currentRequestId;
            currentDialogue = dialogue;
            SMonitor.Log($"Sending speech {dialogue} for voice {voice.Voice}, game {voice.Game} to xVASynth (Req ID: {requestId})");
            
            GameVoiceText text = new GameVoiceText()
            {
                gameId = voice.Game,
                voiceId = voice.Voice,
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
            
            if (requestId != currentRequestId)
                return;

            text.text = dialogue;
            using (StreamWriter file = File.CreateText(speechPath))
            {
                JsonSerializer serializer = new JsonSerializer();
                serializer.Serialize(file, text);
            }
            
            CheckForWav(requestId, 0, voice.Pitch);
        }

        public static async void CheckForWav(int requestId, int currentTicks, float pitch)
        {
            string wavPath = Path.Combine(xVaSynthPath, "output.wav");
            if (File.Exists(wavPath))
            {
                if (requestId != currentRequestId)
                {
                    try { File.Delete(wavPath); } catch { }
                    return;
                }

                SMonitor.Log($"Playing output.wav file");
                bool playSuccessful = false;
                try
                {
                    using (FileStream fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        voiceSound = SoundEffect.FromStream(fs);
                    }
                    playSuccessful = true;
                }
                catch (IOException)
                {
                    await Task.Delay(50);
                    CheckForWav(requestId, currentTicks, pitch);
                    return;
                }
                catch (Exception e) {
                    SMonitor.Log($"Error processing audio: {e.Message}", LogLevel.Error);
                }

                if (playSuccessful)
                {
                    if (requestId != currentRequestId)
                    {
                        try { File.Delete(wavPath); } catch { }
                        return;
                    }

                    try
                    {
                        activeVoiceInstance = voiceSound.CreateInstance();
                        // Dynamically scale pitch natively right before hitting the sound hardware
                        activeVoiceInstance.Pitch = Math.Max(-1.0f, Math.Min(1.0f, pitch));
                        activeVoiceInstance.Play();
                    }
                    catch (Exception e) {
                        SMonitor.Log($"Error playing sound instance: {e.Message}", LogLevel.Error);
                    }
                    
                    try { File.Delete(wavPath); } catch { }
                }
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
            
            CheckForWav(requestId, currentTicks, pitch);
        }
    }
}
