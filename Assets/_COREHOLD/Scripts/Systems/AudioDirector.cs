using System.Collections.Generic;
using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// Central audio playback for the whole game (GDD §10). Web Audio, not FMOD,
    /// so the toolkit is deliberately tiny: volume via <c>SetFloat</c>-equivalent
    /// group gains and clip choice. No AudioMixer effects, no ducking, no filter
    /// sweeps, no reverb zones — those do not work on WebGL and are not used here.
    ///
    /// Design points from the ticket / §10:
    ///
    ///   • <b>Twelve pooled AudioSources.</b> One-shots are played on a free voice.
    ///     When all twelve are busy the <b>oldest</b> voice is stolen rather than
    ///     the new sound refused — a refused shot reads as a bug, a stolen tail
    ///     does not.
    ///
    ///   • <b>One-shot collapse.</b> Identical one-shots fired within 50 ms of each
    ///     other collapse into a single play at slightly raised volume, so four
    ///     Autocannons firing on the same frame are one louder shot rather than
    ///     four dropouts. The window is measured in <b>unscaled</b> time
    ///     (<see cref="Time.unscaledTime"/>) — at the 2× speed toggle (§9.6),
    ///     scaled time would shrink the window and start eating distinct shots.
    ///
    ///   • <b>Volume groups.</b> Master, SFX and Music, each 0..1, multiplied
    ///     together per voice. Wired to a single <see cref="Muted"/> toggle.
    ///
    ///   • <b>Pitch is unscaled.</b> Audio pitch is never tied to Time.timeScale —
    ///     doubling pitch at 2× sounds like a fault (§9.6). One-shots get a small
    ///     per-turret-type ±random pitch instead (GDD §10: ±8% on turret fire).
    ///
    ///   • <b>Rotation loop.</b> The turret rotation loop plays only while a turret
    ///     is actively slewing, and only for the three turrets nearest screen
    ///     centre — ten turrets slewing at once is a wall of noise (§10). That
    ///     policy is driven externally each frame via <see cref="SetRotationLoud"/>.
    ///
    /// Access is through the <see cref="Instance"/> singleton. Every public Play*
    /// method no-ops safely when its clip is unassigned, so gameplay never breaks
    /// while audio is being wired.
    /// </summary>
    [DisallowMultipleComponent]
    public class AudioDirector : MonoBehaviour
    {
        /// <summary>Logical volume group a sound belongs to (GDD §10).</summary>
        public enum Group
        {
            Sfx,
            Music
        }

        /// <summary>One-shot SFX slots the game plays (GDD §10).</summary>
        public enum Sfx
        {
            /// <summary>Kinetic turret fire (Autocannon) — bullet shot.</summary>
            FireKinetic,
            /// <summary>Energy turret fire (Arc Node) — energy shot.</summary>
            FireEnergy,
            /// <summary>Explosive turret fire (Missile Battery) — missile launch.</summary>
            FireMissile,
            /// <summary>Explosive turret fire (Siege Mortar) — heavy shot.</summary>
            FireMortar,
            /// <summary>Small impact where a hitscan / kinetic shot strikes.</summary>
            Impact,
            /// <summary>Splash explosion (Missile / Mortar detonation).</summary>
            Explosion,
            /// <summary>Enemy death.</summary>
            EnemyDeath,
            /// <summary>UI button click (Creepy Cat interface).</summary>
            UIClick,
            /// <summary>Turret placed on a hardpoint (build puff).</summary>
            Build,
            /// <summary>Core alarm when the Core takes a leak hit (Creepy Cat).</summary>
            CoreAlarm,
            /// <summary>Kill-streak step (R2) — replayed with a rising pitch scale as the streak grows. APPEND-ONLY: enum values are serialized by index in the scene.</summary>
            StreakStep,
            /// <summary>"CLOSE CALL" sting when a wave ends with the Core nearly lost (R3).</summary>
            CloseCall
        }

        [System.Serializable]
        private struct SfxEntry
        {
            [Tooltip("Which logical one-shot this clip fulfils (GDD §10).")]
            public Sfx id;

            [Tooltip("The AudioClip. Import as CompressedInMemory on the WebGL override (GDD §10).")]
            public AudioClip clip;

            [Tooltip("Base linear volume (0..1) for this sound before group gains.")]
            [Range(0f, 1f)] public float volume;

            [Tooltip("Random pitch spread applied each play, e.g. 0.08 = ±8% (GDD §10 turret fire).")]
            [Range(0f, 0.5f)] public float pitchSpread;
        }

        [Header("Voices (GDD §10) — twelve pooled AudioSources")]
        [Tooltip("Number of pooled one-shot voices. GDD §10 fixes this at twelve.")]
        [SerializeField] private int voiceCount = 12;

        [Header("One-shot collapse (GDD §10)")]
        [Tooltip("Identical one-shots fired within this many seconds of UNSCALED time collapse into one play. GDD §10: 50 ms.")]
        [SerializeField] private float collapseWindow = 0.05f;

        [Tooltip("Volume multiplier applied to a collapsed one-shot so a stacked salvo reads slightly louder, not identical.")]
        [Range(1f, 2f)] [SerializeField] private float collapseVolumeBoost = 1.25f;

        [Header("Volume groups (GDD §10) — 0..1, no AudioMixer effects")]
        [Range(0f, 1f)] [SerializeField] private float masterVolume = 1f;
        [Range(0f, 1f)] [SerializeField] private float sfxVolume = 0.9f;
        [Range(0f, 1f)] [SerializeField] private float musicVolume = 0.6f;
        [SerializeField] private bool muted;

        [Header("One-shot clips (GDD §10) — assign from Turret SFX / Creepy Cat")]
        [SerializeField]
        private SfxEntry[] sfx =
        {
            new SfxEntry { id = Sfx.FireKinetic, volume = 0.9f, pitchSpread = 0.08f },
            new SfxEntry { id = Sfx.FireEnergy,  volume = 0.9f, pitchSpread = 0.08f },
            new SfxEntry { id = Sfx.FireMissile, volume = 0.9f, pitchSpread = 0.08f },
            new SfxEntry { id = Sfx.FireMortar,  volume = 1.0f, pitchSpread = 0.08f },
            new SfxEntry { id = Sfx.Impact,      volume = 0.6f, pitchSpread = 0.06f },
            new SfxEntry { id = Sfx.Explosion,   volume = 0.9f, pitchSpread = 0.06f },
            new SfxEntry { id = Sfx.EnemyDeath,  volume = 0.7f, pitchSpread = 0.06f },
            new SfxEntry { id = Sfx.UIClick,     volume = 0.8f, pitchSpread = 0f },
            new SfxEntry { id = Sfx.Build,       volume = 0.8f, pitchSpread = 0f },
            new SfxEntry { id = Sfx.CoreAlarm,   volume = 1.0f, pitchSpread = 0f },
            new SfxEntry { id = Sfx.StreakStep,  volume = 0.8f, pitchSpread = 0f },
            new SfxEntry { id = Sfx.CloseCall,   volume = 0.9f, pitchSpread = 0f },
        };

        [Header("Turret rotation loop (GDD §10)")]
        [Tooltip("Rotation loop clip, played only while a turret slews and only for the three turrets nearest screen centre.")]
        [SerializeField] private AudioClip rotationLoopClip;

        [Tooltip("Base volume of a single rotation loop voice.")]
        [Range(0f, 1f)] [SerializeField] private float rotationLoopVolume = 0.465f;   // shipped Game.unity value

        [Tooltip("How many turrets may sound the rotation loop at once (nearest screen centre). GDD §10: three.")]
        [SerializeField] private int rotationLoopVoices = 3;

        [Header("Music (GDD §4.9 — optional; ambient bed if no track)")]
        [Tooltip("Looping music/ambient track. Started with StartMusic() after the title Play gesture (GDD §10).")]
        [SerializeField] private AudioClip musicClip;

        // ----- Singleton -----

        private static AudioDirector _instance;

        /// <summary>The active director, if one exists.</summary>
        public static AudioDirector Instance => _instance;

        // ----- Runtime -----

        private readonly Dictionary<Sfx, SfxEntry> _clips = new Dictionary<Sfx, SfxEntry>();

        // Pooled one-shot voices and, per voice, the unscaled time it started —
        // used to find the OLDEST voice to steal when all are busy (GDD §10).
        private AudioSource[] _voices;
        private float[] _voiceStartTime;

        // Last-play time (unscaled) keyed by logical sound, for the 50 ms collapse.
        private readonly Dictionary<Sfx, float> _lastPlay = new Dictionary<Sfx, float>();

        // Last-play time (unscaled) keyed by raw clip, for the 50 ms collapse on
        // ad-hoc clips played through PlayClip (e.g. per-enemy fire/death sounds).
        private readonly Dictionary<AudioClip, float> _lastPlayClip = new Dictionary<AudioClip, float>();

        // Dedicated rotation-loop voices (never stolen by one-shots).
        private AudioSource[] _rotationVoices;

        // Dedicated music voice (never stolen).
        private AudioSource _musicSource;

        // ----- Volume group properties -----

        /// <summary>Master gain (0..1). Multiplies every group (GDD §10).</summary>
        public float MasterVolume
        {
            get => masterVolume;
            set { masterVolume = Mathf.Clamp01(value); ApplyGroupVolumes(); }
        }

        /// <summary>SFX group gain (0..1).</summary>
        public float SfxVolume
        {
            get => sfxVolume;
            set { sfxVolume = Mathf.Clamp01(value); }
        }

        /// <summary>Music group gain (0..1).</summary>
        public float MusicVolume
        {
            get => musicVolume;
            set { musicVolume = Mathf.Clamp01(value); ApplyGroupVolumes(); }
        }

        /// <summary>
        /// Global mute toggle wired to the UI (GDD §10). When true every group is
        /// silenced without losing the individual slider values, so unmuting
        /// restores them exactly.
        /// </summary>
        public bool Muted
        {
            get => muted;
            set { muted = value; ApplyGroupVolumes(); }
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }
            _instance = this;

            BuildClipLookup();
            BuildVoices();
            BuildRotationVoices();
            BuildMusicSource();
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        private void BuildClipLookup()
        {
            _clips.Clear();
            if (sfx == null)
                return;
            foreach (var e in sfx)
            {
                if (!_clips.ContainsKey(e.id))
                    _clips.Add(e.id, e);
            }
        }

        private void BuildVoices()
        {
            int count = Mathf.Max(1, voiceCount);
            _voices = new AudioSource[count];
            _voiceStartTime = new float[count];

            var root = new GameObject("Voices");
            root.transform.SetParent(transform, false);

            for (int i = 0; i < count; i++)
            {
                var go = new GameObject($"Voice_{i}");
                go.transform.SetParent(root.transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.loop = false;
                src.spatialBlend = 0f; // 2D — fixed camera, no positional audio needed (§5.1).
                _voices[i] = src;
                _voiceStartTime[i] = -1f;
            }
        }

        private void BuildRotationVoices()
        {
            int count = Mathf.Max(0, rotationLoopVoices);
            _rotationVoices = new AudioSource[count];
            if (count == 0)
                return;

            var root = new GameObject("RotationLoop");
            root.transform.SetParent(transform, false);

            for (int i = 0; i < count; i++)
            {
                var go = new GameObject($"Rotation_{i}");
                go.transform.SetParent(root.transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.loop = true;
                src.spatialBlend = 0f;
                src.clip = rotationLoopClip;
                src.volume = 0f; // faded in per frame while slewing (SetRotationLoud).
                _rotationVoices[i] = src;
            }
        }

        // ================================================================================
        //  Weather ambience loop (R14 follow-on)
        // ================================================================================

        private AudioSource _weatherSource;
        private float _weatherBaseVolume;
        private AudioClip _procRainLoop;
        private AudioClip _procWindLoop;

        /// <summary>
        /// Start the weather ambience: the authored clip when one is assigned, else
        /// a SYNTHESIZED loop — filtered noise shaped as rain hiss or a slow-gusting
        /// wind — so weather is audible with zero audio assets. Volume rides the
        /// music group gain, so mute and the volume sliders behave as expected.
        /// </summary>
        public void PlayWeatherLoop(AudioClip authored, bool rain, float volume)
        {
            if (_weatherSource == null)
            {
                var go = new GameObject("WeatherAmbience");
                go.transform.SetParent(transform, false);
                _weatherSource = go.AddComponent<AudioSource>();
                _weatherSource.playOnAwake = false;
                _weatherSource.loop = true;
                _weatherSource.spatialBlend = 0f;
            }

            AudioClip clip = authored != null ? authored : ProceduralWeatherLoop(rain);
            _weatherBaseVolume = Mathf.Clamp01(volume);
            if (_weatherSource.clip != clip)
            {
                _weatherSource.clip = clip;
                _weatherSource.Play();
            }
            else if (!_weatherSource.isPlaying)
            {
                _weatherSource.Play();
            }
            ApplyGroupVolumes();
        }

        /// <summary>Stop the weather ambience (preset cleared, scene torn down).</summary>
        public void StopWeatherLoop()
        {
            if (_weatherSource != null)
                _weatherSource.Stop();
            _weatherBaseVolume = 0f;
        }

        /// <summary>
        /// A seamless 4-second ambience loop, synthesized once and cached. Rain is
        /// broadband noise low-passed to a hiss with a faint patter shimmer; wind is
        /// heavily low-passed noise swelling on a slow gust cycle. The last quarter
        /// second is crossfaded into the first so the loop point is inaudible.
        /// Fixed seed: this is audio, not gameplay, but there is no reason for two
        /// machines to sound different either.
        /// </summary>
        private AudioClip ProceduralWeatherLoop(bool rain)
        {
            AudioClip cached = rain ? _procRainLoop : _procWindLoop;
            if (cached != null)
                return cached;

            const int rate = 32768;
            const int seconds = 4;
            int n = rate * seconds;
            var data = new float[n];
            var rng = new System.Random(rain ? 41213 : 90210);

            float lp = 0f, lp2 = 0f;
            for (int i = 0; i < n; i++)
            {
                float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                if (rain)
                {
                    // One-pole low-pass ≈ 5 kHz: white → hiss. The 1.7 Hz wobble
                    // keeps it organic instead of a flat broadcast-static tone.
                    lp += 0.55f * (white - lp);
                    float wobble = 1f + 0.12f * Mathf.Sin(2f * Mathf.PI * 1.7f * i / rate);
                    data[i] = lp * 0.5f * wobble;
                }
                else
                {
                    // Two cascaded low-passes ≈ 350 Hz: white → wind body, swelling
                    // on a 0.15 Hz gust cycle with a shallower 0.9 Hz flutter.
                    lp += 0.065f * (white - lp);
                    lp2 += 0.065f * (lp - lp2);
                    float gust = 0.62f + 0.38f * Mathf.Sin(2f * Mathf.PI * 0.15f * i / rate);
                    float flutter = 1f + 0.08f * Mathf.Sin(2f * Mathf.PI * 0.9f * i / rate);
                    data[i] = lp2 * 2.4f * gust * flutter;
                }
            }

            int fade = rate / 4;
            for (int i = 0; i < fade; i++)
            {
                float t = i / (float)fade;
                data[n - fade + i] = data[n - fade + i] * (1f - t) + data[i] * t;
            }

            var clip = AudioClip.Create(rain ? "WeatherLoop_Rain (synth)" : "WeatherLoop_Wind (synth)",
                                        n, 1, rate, false);
            clip.SetData(data, 0);
            if (rain) _procRainLoop = clip; else _procWindLoop = clip;
            return clip;
        }

        private void BuildMusicSource()
        {
            var go = new GameObject("Music");
            go.transform.SetParent(transform, false);
            _musicSource = go.AddComponent<AudioSource>();
            _musicSource.playOnAwake = false;
            _musicSource.loop = true;
            _musicSource.spatialBlend = 0f;
            _musicSource.clip = musicClip;
            ApplyGroupVolumes();
        }

        // ================================================================================
        //  Volume groups
        // ================================================================================

        private float SfxGain => (muted ? 0f : 1f) * masterVolume * sfxVolume;
        private float MusicGain => (muted ? 0f : 1f) * masterVolume * musicVolume;

        /// <summary>
        /// Push the current group gains onto looping voices (music, rotation loop).
        /// One-shots read the gain at play time, so they need no update here.
        /// </summary>
        private void ApplyGroupVolumes()
        {
            if (_musicSource != null)
                _musicSource.volume = MusicGain;

            if (_weatherSource != null)
                _weatherSource.volume = MusicGain * _weatherBaseVolume;

            // Rotation-loop voices keep their own per-voice loudness (0 or full),
            // scaled by the SFX group gain — handled in SetRotationLoud each frame,
            // but reflect an immediate mute here too.
            if (_rotationVoices != null && muted)
            {
                foreach (var v in _rotationVoices)
                    if (v != null) v.volume = 0f;
            }
        }

        // ================================================================================
        //  One-shot playback (GDD §10) — voice stealing + 50 ms collapse
        // ================================================================================

        /// <summary>
        /// Play a logical one-shot (GDD §10). Applies the ±spread random pitch, the
        /// 50 ms unscaled collapse (a repeat inside the window re-triggers the same
        /// voice slightly louder rather than starting a new one), and voice stealing
        /// (the oldest voice is reused when all are busy). No-op if the clip is unset.
        /// </summary>
        public void Play(Sfx id) => Play(id, 1f);

        /// <summary>Play a one-shot with an extra volume scale (0..1+).</summary>
        public void Play(Sfx id, float volumeScale) => Play(id, volumeScale, 1f);

        /// <summary>
        /// Play a one-shot with an extra volume scale AND a pitch scale (R2 —
        /// the kill-streak step rises in pitch per step). The pitch scale
        /// multiplies the entry's ±spread random pitch and is clamped to a
        /// musical range so a runaway streak never chipmunks the mix.
        /// </summary>
        public void Play(Sfx id, float volumeScale, float pitchScale)
        {
            if (!_clips.TryGetValue(id, out SfxEntry entry) || entry.clip == null)
                return;

            float now = Time.unscaledTime;

            // Collapse: an identical one-shot inside the window folds into the most
            // recent play at a slightly raised volume instead of taking a new voice
            // (GDD §10). Window is unscaled so 2× speed does not eat distinct shots.
            if (_lastPlay.TryGetValue(id, out float last) && (now - last) <= collapseWindow)
            {
                AudioSource recent = FindPlayingVoice(entry.clip);
                if (recent != null)
                {
                    float boosted = recent.volume * collapseVolumeBoost;
                    recent.volume = Mathf.Min(1f, boosted);
                    _lastPlay[id] = now;
                    return;
                }
                // No live voice found (already finished) — fall through to a fresh play.
            }

            _lastPlay[id] = now;

            AudioSource voice = AcquireVoice(now);
            if (voice == null)
                return;

            voice.clip = entry.clip;
            voice.volume = Mathf.Clamp01(entry.volume * Mathf.Max(0f, volumeScale)) * SfxGain;
            voice.pitch = Mathf.Clamp(RandomPitch(entry.pitchSpread) * pitchScale, 0.5f, 2.5f);
            voice.Play();
        }

        /// <summary>
        /// Play an arbitrary <see cref="AudioClip"/> through the pooled SFX voices
        /// (GDD §10). Used for per-instance sounds that are not one of the fixed
        /// logical <see cref="Sfx"/> slots — e.g. an enemy's own fire/death clip
        /// authored on its <c>EnemyDefinition</c>. Applies the same ±spread random
        /// pitch, 50 ms unscaled collapse (keyed by clip) and voice stealing as
        /// <see cref="Play(Sfx)"/>. No-op when the clip is null.
        /// </summary>
        public void PlayClip(AudioClip clip, float volume = 1f, float pitchSpread = 0f)
        {
            if (clip == null)
                return;

            float now = Time.unscaledTime;

            // Collapse identical clips fired within the window into the most recent
            // live voice at a slightly raised volume (GDD §10). Unscaled window.
            if (_lastPlayClip.TryGetValue(clip, out float last) && (now - last) <= collapseWindow)
            {
                AudioSource recent = FindPlayingVoice(clip);
                if (recent != null)
                {
                    recent.volume = Mathf.Min(1f, recent.volume * collapseVolumeBoost);
                    _lastPlayClip[clip] = now;
                    return;
                }
            }

            _lastPlayClip[clip] = now;

            AudioSource voice = AcquireVoice(now);
            if (voice == null)
                return;

            voice.clip = clip;
            voice.volume = Mathf.Clamp01(volume) * SfxGain;
            voice.pitch = RandomPitch(pitchSpread);
            voice.Play();
        }

        /// <summary>±spread random pitch (GDD §10: ±8% on turret fire). Unscaled — never tied to timeScale (§9.6).</summary>
        private float RandomPitch(float spread)
        {
            if (spread <= 0f)
                return 1f;
            return 1f + Random.Range(-spread, spread);
        }

        /// <summary>
        /// Find a free voice, or steal the OLDEST currently-playing one (GDD §10) so
        /// a new sound is never refused. Returns null only when no voices exist.
        /// </summary>
        private AudioSource AcquireVoice(float now)
        {
            if (_voices == null || _voices.Length == 0)
                return null;

            int oldestIndex = 0;
            float oldestStart = float.MaxValue;

            for (int i = 0; i < _voices.Length; i++)
            {
                AudioSource v = _voices[i];
                if (v == null)
                    continue;

                if (!v.isPlaying)
                {
                    _voiceStartTime[i] = now;
                    return v;
                }

                if (_voiceStartTime[i] < oldestStart)
                {
                    oldestStart = _voiceStartTime[i];
                    oldestIndex = i;
                }
            }

            // All busy — steal the oldest voice (a stolen tail beats a refused shot).
            AudioSource stolen = _voices[oldestIndex];
            if (stolen != null)
            {
                stolen.Stop();
                _voiceStartTime[oldestIndex] = now;
            }
            return stolen;
        }

        /// <summary>Return the most recent still-playing voice using the given clip, or null.</summary>
        private AudioSource FindPlayingVoice(AudioClip clip)
        {
            if (_voices == null)
                return null;

            AudioSource best = null;
            float bestStart = float.MinValue;
            for (int i = 0; i < _voices.Length; i++)
            {
                AudioSource v = _voices[i];
                if (v == null || !v.isPlaying || v.clip != clip)
                    continue;
                if (_voiceStartTime[i] > bestStart)
                {
                    bestStart = _voiceStartTime[i];
                    best = v;
                }
            }
            return best;
        }

        // ---- Convenience wrappers for gameplay call sites ----

        /// <summary>Fire sound for a turret's damage type / weapon family (GDD §10).</summary>
        public void PlayFire(Data.DamageType type, bool isProjectile, bool isMortar)
        {
            Sfx id;
            switch (type)
            {
                case Data.DamageType.Energy:
                    id = Sfx.FireEnergy;
                    break;
                case Data.DamageType.Explosive:
                    id = isMortar ? Sfx.FireMortar : Sfx.FireMissile;
                    break;
                default:
                    id = Sfx.FireKinetic;
                    break;
            }
            Play(id);
        }

        /// <summary>Impact spark sound where a shot strikes a unit (GDD §10).</summary>
        public void PlayImpact() => Play(Sfx.Impact);

        /// <summary>Splash explosion sound (GDD §10).</summary>
        public void PlayExplosion() => Play(Sfx.Explosion);

        /// <summary>Shared enemy death sound (GDD §10). Used when an enemy has no authored death clip.</summary>
        public void PlayEnemyDeath() => Play(Sfx.EnemyDeath);

        /// <summary>
        /// Enemy fire sound authored on an <c>EnemyDefinition</c> (GDD §10). No-op
        /// when the definition or its clip is null, so silent enemies stay silent.
        /// </summary>
        public void PlayEnemyFire(Data.EnemyDefinition def)
        {
            if (def != null && def.fireSound != null)
                PlayClip(def.fireSound, def.fireVolume, def.firePitchSpread);
        }

        /// <summary>
        /// Enemy death sound (GDD §10). Plays the per-enemy clip authored on the
        /// <c>EnemyDefinition</c> when present, otherwise falls back to the shared
        /// <see cref="Sfx.EnemyDeath"/> one-shot.
        /// </summary>
        public void PlayEnemyDeath(Data.EnemyDefinition def)
        {
            if (def != null && def.deathSound != null)
                PlayClip(def.deathSound, def.deathVolume, def.deathPitchSpread);
            else
                Play(Sfx.EnemyDeath);
        }

        /// <summary>UI click (GDD §10).</summary>
        public void PlayUIClick() => Play(Sfx.UIClick);

        /// <summary>Turret-placed build sound (GDD §10).</summary>
        public void PlayBuild() => Play(Sfx.Build);

        /// <summary>Core alarm when the Core takes a leak hit (GDD §10).</summary>
        public void PlayCoreAlarm() => Play(Sfx.CoreAlarm);

        // ================================================================================
        //  Turret rotation loop (GDD §10) — nearest three to screen centre, while slewing
        // ================================================================================

        /// <summary>
        /// Set how many turrets are currently "loud" — i.e. slewing and among the
        /// three nearest screen centre (GDD §10). Called once per frame by the
        /// rotation-loop policy (see <see cref="TurretRotationAudio"/>). The
        /// director fades that many dedicated loop voices up and the rest down, so
        /// the loop plays only while turrets slew and never for more than the
        /// configured number of voices.
        /// </summary>
        public void SetRotationLoud(int loudCount)
        {
            if (_rotationVoices == null)
                return;

            int target = Mathf.Clamp(loudCount, 0, _rotationVoices.Length);
            float gain = SfxGain * rotationLoopVolume;

            for (int i = 0; i < _rotationVoices.Length; i++)
            {
                AudioSource v = _rotationVoices[i];
                if (v == null)
                    continue;

                bool loud = i < target;
                if (loud)
                {
                    if (v.clip == null)
                        v.clip = rotationLoopClip;
                    if (v.clip != null && !v.isPlaying)
                        v.Play();
                    v.volume = gain;
                }
                else
                {
                    v.volume = 0f;
                    if (v.isPlaying && v.clip == null)
                        v.Stop();
                }
            }
        }

        /// <summary>Number of dedicated rotation-loop voices (GDD §10: three).</summary>
        public int RotationLoopVoices => _rotationVoices != null ? _rotationVoices.Length : 0;

        // ================================================================================
        //  Music (GDD §4.9) — started after the title Play gesture (GDD §10)
        // ================================================================================

        /// <summary>Start (or restart) the looping music/ambient bed (GDD §10).</summary>
        public void StartMusic()
        {
            if (_musicSource == null)
                return;
            if (_musicSource.clip == null)
                _musicSource.clip = musicClip;
            if (_musicSource.clip == null)
                return;

            _musicSource.volume = MusicGain;
            if (!_musicSource.isPlaying)
                _musicSource.Play();
        }

        /// <summary>Stop the music bed.</summary>
        public void StopMusic()
        {
            if (_musicSource != null && _musicSource.isPlaying)
                _musicSource.Stop();
        }

        /// <summary>Swap the music clip at runtime (e.g. the boss track) and play it.</summary>
        public void SetMusic(AudioClip clip, bool play = true)
        {
            musicClip = clip;
            if (_musicSource == null)
                return;
            _musicSource.clip = clip;
            if (play)
                StartMusic();
        }
    }
}
