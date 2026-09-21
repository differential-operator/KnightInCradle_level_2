using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;

namespace KnightInCradle
{
    /// <summary>
    /// 音效播放（冲刺 / 挥砍 / 命中 / 受击 / 凝聚等）。
    /// 音效以嵌入式资源打包在模组 DLL 内；播放走自研的 waveOut 混音器：
    /// - 多段音效可以同时播放（连续凝聚回血时，上一段回血音不会被下一段截断）；
    /// - 支持循环音（凝聚持续音）与按分组精确停止（回血音只在受击/死亡时被强制停止）。
    /// 音量通过预先缩放 16bit PCM 采样实现。
    /// </summary>
    internal static class DashAudio
    {
        private const int FocusChargeGroup = 1; // 凝聚持续音分组
        private const int FocusHealGroup = 2;   // 回血音分组（受击时才停）
        private const int SuperLoopGroup = 3;   // 超级冲刺飞行循环音分组
        private const int DiveLoopGroup = 4;    // 黑暗降临下落循环音分组
        private const int NailArtChargeGroup = 5; // 骨钉技艺蓄满循环音分组
        private const int DreamNailChargeGroup = 6; // 梦钉短蓄力音分组
        private const int DreamNailSlashGroup = 7; // 梦钉抽出音分组
        private const int GrimmIdleGroup = 8;      // 小格林待机循环音分组

        private static byte[] _dashWav;
        private static byte[] _shadowDashWav;
        private static readonly byte[][] _swingWavs = new byte[3][];
        private static byte[] _hitWav;
        private static byte[] _enemyHitWav;
        private static byte[] _spikeWav;
        private static byte[] _wingWav;
        private static byte[] _hurtWav;
        private static byte[] _deathWav;
        private static byte[] _focusChargeWav;
        private static byte[] _focusHealWav;
        private static byte[] _superChargeWav;
        private static byte[] _superReadyWav;
        private static byte[] _superLoopWav;
        private static byte[] _superBurstWav;
        private static byte[] _superBrakeWav;
        private static byte[] _superImpactWav;
        private static byte[] _fireballWav;
        private static byte[] _shadowQuakeWav;
        private static byte[] _diveLoopWav;
        private static byte[] _screamWav;
        private static byte[] _nailArtGreatSlashWav;
        private static byte[] _nailArtChargeInitiateWav;
        private static byte[] _nailArtChargeCompleteWav;
        private static byte[] _nailArtChargeLoopWav;
        private static byte[] _nailArtCycloneWav;
        private static byte[] _dreamNailChargeWav;
        private static byte[] _dreamNailSlashWav;
        private static byte[] _elegyBladeWav;
        private static byte[] _heavyKillWav;
        private static byte[] _flukeCastWav;
        private static readonly byte[][] _flukeBounceWavs = new byte[12][];
        private static byte[] _uterusExplosionWav;
        private static byte[] _grimmIdleWav;
        private static byte[] _grimmYelp4Wav;
        private static byte[] _grimmYelp5Wav;
        private static byte[] _tuneWav; // 护符43 无忧旋律：免伤守护之歌
        // ---- 虚空解放（梦之门技能）音效 ----
        private static byte[] _voidChargeWav;    // hero_dream_nail_short_charge
        private static byte[] _voidChallengeWav; // radiance_challenge（与蓄力音同时播放）
        private static byte[] _voidKnockDownWav; // radiance_knock_down（出伤瞬间）

        private const int VoidChallengeGroup = 5; // 与蓄力音分通道，保证同时播放
        private static readonly System.Random _flukeRnd = new System.Random();
        private static int _swingIndex;
        private static WavMixer _mixer;

        public static void Init(ConfigEntry<float> dashVolume, ConfigEntry<float> shadowVolume)
        {
            _dashWav = LoadScaled("KnightInCradle.hero_dash.wav", dashVolume != null ? dashVolume.Value : 0.5f);
            _shadowDashWav = LoadScaled("KnightInCradle.hero_shade_dash_1.wav", shadowVolume != null ? shadowVolume.Value : 1f);
            _swingWavs[0] = LoadScaled("KnightInCradle.sword_2.wav", 0.75f);
            _swingWavs[1] = LoadScaled("KnightInCradle.sword_3.wav", 0.75f);
            _swingWavs[2] = LoadScaled("KnightInCradle.sword_4.wav", 0.75f);
            _hitWav = LoadScaled("KnightInCradle.sword_hit_window_1.wav", 0.9f);
            _enemyHitWav = LoadScaled("KnightInCradle.enemy_damage.wav", 0.9f);
            _spikeWav = LoadScaled("KnightInCradle.spike_pogo.wav", 0.9f);
            _wingWav = LoadScaled("KnightInCradle.hero_wings.wav", 0.85f);
            _hurtWav = LoadScaled("KnightInCradle.hero_damage.wav", 1.8f);
            _deathWav = LoadScaled("KnightInCradle.hero_death_v2.wav", 1.8f);
            _focusChargeWav = LoadScaled("KnightInCradle.focus_health_charging.wav", 0.5f);
            _focusHealWav = LoadScaled("KnightInCradle.focus_health_heal.wav", 1f);
            _superChargeWav = LoadScaled("KnightInCradle.hero_super_dash_charge.wav", 0.7f);
            _superReadyWav = LoadScaled("KnightInCradle.hero_super_dash_ready.wav", 0.9f);
            _superLoopWav = LoadScaled("KnightInCradle.hero_super_dash_loop.wav", 0.7f);
            _superBurstWav = LoadScaled("KnightInCradle.hero_super_dash_burst.wav", 0.9f);
            _superBrakeWav = LoadScaled("KnightInCradle.hero_super_dash_air_brake.wav", 0.8f);
            _superImpactWav = LoadScaled("KnightInCradle.hero_super_dash_impact_wall.wav", 0.8f);
            _fireballWav = LoadScaled("KnightInCradle.shadow_soul.wav", 1f);
            _shadowQuakeWav = LoadScaled("KnightInCradle.shadow_quake.wav", 1f);
            _diveLoopWav = LoadScaled("KnightInCradle.dive_loop.wav", 1f);
            _screamWav = LoadScaled("KnightInCradle.scream.wav", 1f);
            _nailArtGreatSlashWav = LoadScaled("KnightInCradle.nail_art_great_slash.wav", 1f);
            _nailArtChargeInitiateWav = LoadScaled("KnightInCradle.nail_art_charge_initiate.wav", 1f);
            _nailArtChargeCompleteWav = LoadScaled("KnightInCradle.nail_art_charge_complete.wav", 1f);
            _nailArtChargeLoopWav = LoadScaled("KnightInCradle.nail_art_charge_loop.wav", 1f);
            _nailArtCycloneWav = LoadScaled("KnightInCradle.nail_art_cyclone_slash.wav", 1f);
            _dreamNailChargeWav = LoadScaled("KnightInCradle.dream_nail_charge.wav", 1f);
            _dreamNailSlashWav = LoadScaled("KnightInCradle.dream_nail_slash.wav", 1f);
            _elegyBladeWav = LoadScaled("KnightInCradle.soul_totem_slash.wav", 0.75f);
            _heavyKillWav = LoadScaled("KnightInCradle.radiance_damage_from_hero_tentacle.wav", 2f);
            // 吸虫之巢：释放音效 + 12 个落地弹跳音效
            _flukeCastWav = LoadScaled("KnightInCradle.hero_fluke_cast.wav", 1f);
            for (int fi = 0; fi < _flukeBounceWavs.Length; fi++)
            {
                _flukeBounceWavs[fi] = LoadScaled(
                    "KnightInCradle.hero_fluke_bounce_" + (fi + 1) + ".wav", 1f);
            }
            // 发光子宫：幼体碰撞爆炸音效
            _uterusExplosionWav = LoadScaled("KnightInCradle.explosion_4_wet.wav", 2f);
            _swingIndex = 0;
            if (_mixer == null)
            {
                _mixer = new WavMixer();
                _mixer.Start();
            }
        }

        private static byte[] LoadScaled(string resourceName, float volume)
        {
            byte[] bytes = ReadResource(resourceName);
            if (bytes == null)
            {
                return null;
            }
            return volume != 1f ? ScaleWav(bytes, volume) : bytes;
        }

        /// <summary>播放冲刺音效。shadow=true 播暗影冲刺，否则播普通冲刺。</summary>
        public static void PlayDash(bool shadow)
        {
            PlayOnce(shadow ? _shadowDashWav : _dashWav);
        }

        /// <summary>播放蜕变挽歌剑气发射音效。</summary>
        public static void PlayElegyBlade()
        {
            PlayOnce(_elegyBladeWav);
        }

        /// <summary>播放沉重之击斩杀音效。</summary>
        public static void PlayHeavyKill()
        {
            PlayOnce(_heavyKillWav);
        }

        /// <summary>播放挥出骨钉的音效（三连循环：sword_2 → sword_3 → sword_4）。</summary>
        public static void PlaySlashSwing()
        {
            if (_swingWavs.Length == 0 || _swingWavs[_swingIndex] == null)
            {
                return;
            }
            PlayOnce(_swingWavs[_swingIndex]);
            _swingIndex = (_swingIndex + 1) % _swingWavs.Length;
        }

        /// <summary>播放骨钉击中怪物的音效。</summary>
        public static void PlaySlashHit()
        {
            PlayOnce(_hitWav);
        }

        /// <summary>播放敌人受击音效（空洞骑士原版 enemy_damage）。</summary>
        public static void PlayEnemyHit()
        {
            PlayOnce(_enemyHitWav);
        }

        /// <summary>播放下劈命中尖刺/荆棘时的专属“叮”音效（空洞骑士原版 spike_pogo）。</summary>
        public static void PlaySpikePogo()
        {
            PlayOnce(_spikeWav != null ? _spikeWav : _hitWav);
        }

        /// <summary>播放二段跳翅膀扇动音效（空洞骑士原版 hero_wings）。</summary>
        public static void PlayWingFlap()
        {
            PlayOnce(_wingWav);
        }

        /// <summary>播放小骑士受击音效（空洞骑士原版 hero_damage）。</summary>
        public static void PlayHurt()
        {
            PlayOnce(_hurtWav);
        }

        /// <summary>播放小骑士死亡音效（空洞骑士原版 hero_death_v2）。</summary>
        public static void PlayDeath()
        {
            PlayOnce(_deathWav);
        }

        /// <summary>开始播放凝聚持续音（循环，凝聚结束时停止）。</summary>
        public static void PlayFocusCharge()
        {
            if (_mixer == null || _focusChargeWav == null)
            {
                return;
            }
            _mixer.Play(_focusChargeWav, FocusChargeGroup, true);
        }

        /// <summary>停止凝聚持续音（只停凝聚循环音，不影响其它音效）。</summary>
        public static void StopFocusCharge()
        {
            if (_mixer != null)
            {
                _mixer.StopGroup(FocusChargeGroup);
            }
        }

        /// <summary>
        /// 播放凝聚回血完成音效。走混音器独立通道：连续回血时上一段完整播完；
        /// 只有受击/死亡（StopFocusHeal）才会强制停止。
        /// </summary>
        public static void PlayFocusHeal()
        {
            if (_mixer == null || _focusHealWav == null)
            {
                return;
            }
            _mixer.Play(_focusHealWav, FocusHealGroup, false);
        }

        /// <summary>强制停止所有回血音（受击/死亡打断时调用）。</summary>
        public static void StopFocusHeal()
        {
            if (_mixer != null)
            {
                _mixer.StopGroup(FocusHealGroup);
            }
        }

        /// <summary>超级冲刺开始蓄能音效（hero_super_dash_charge）。</summary>
        public static void PlaySuperCharge()
        {
            PlayOnce(_superChargeWav);
        }

        /// <summary>超级冲刺蓄满能量提示音（hero_super_dash_ready）。</summary>
        public static void PlaySuperReady()
        {
            PlayOnce(_superReadyWav);
        }

        /// <summary>超级冲刺飞行循环音（开始发射时播放，停止/撞墙/受击时停止）。</summary>
        public static void PlaySuperLoop()
        {
            if (_mixer == null || _superLoopWav == null)
            {
                return;
            }
            _mixer.Play(_superLoopWav, SuperLoopGroup, true);
        }

        /// <summary>停止超级冲刺飞行循环音。</summary>
        public static void StopSuperLoop()
        {
            if (_mixer != null)
            {
                _mixer.StopGroup(SuperLoopGroup);
            }
        }

        /// <summary>超级冲刺发射瞬间的爆发音效（hero_super_dash_burst）。</summary>
        public static void PlaySuperBurst()
        {
            PlayOnce(_superBurstWav);
        }

        /// <summary>超级冲刺主动停止（惯性）音效（hero_super_dash_air_brake）。</summary>
        public static void PlaySuperBrake()
        {
            PlayOnce(_superBrakeWav);
        }

        /// <summary>超级冲刺撞墙/受阻音效（hero_super_dash_impact_wall）。</summary>
        public static void PlaySuperImpact()
        {
            PlayOnce(_superImpactWav);
        }

        /// <summary>暗影之魂发射音效（HK 火球音效待提取，暂为空占位）。</summary>
        public static void PlayFireballCast()
        {
            PlayOnce(_fireballWav);
        }

        /// <summary>吸虫之巢：释放暗影之魂时播放吸虫释放音效（hero_fluke_cast）。</summary>
        public static void PlayFlukeCast()
        {
            PlayOnce(_flukeCastWav);
        }

        /// <summary>吸虫之巢：吸虫落地弹跳音效（hero_fluke_bounce_1~12 随机）。</summary>
        public static void PlayFlukeBounce()
        {
            PlayOnce(_flukeBounceWavs[_flukeRnd.Next(_flukeBounceWavs.Length)]);
        }

        /// <summary>发光子宫：幼体碰撞爆炸音效（explosion_4_wet）。</summary>
        public static void PlayUterusExplosion()
        {
            PlayOnce(_uterusExplosionWav);
        }

        /// <summary>暗影之魂命中敌人：沿用敌人受击音效。</summary>
        public static void PlayFireballHit()
        {
            PlayEnemyHit();
        }

        /// <summary>黑暗降临（下砸）落地音效（用户录制 shadow_quake）。</summary>
        public static void PlayDiveLand()
        {
            PlayOnce(_shadowQuakeWav);
        }

        /// <summary>黑暗降临（下砸）下落循环音效：从下砸开始循环播放，直到落地停止。</summary>
        public static void PlayDiveLoop()
        {
            if (_mixer == null || _diveLoopWav == null)
            {
                return;
            }
            _mixer.Play(_diveLoopWav, DiveLoopGroup, true);
        }

        /// <summary>停止黑暗降临下落循环音效（落地 / 死亡 / 切回诺艾尔 / 过图时调用）。</summary>
        public static void StopDiveLoop()
        {
            if (_mixer != null)
            {
                _mixer.StopGroup(DiveLoopGroup);
            }
        }

        /// <summary>深渊尖啸释放音效（用户录制，一次性播放）。</summary>
        public static void PlayScreamCast()
        {
            PlayOnce(_screamWav);
        }

        /// <summary>骨钉技艺·强力劈砍释放音效（用户提取 hero_nail_art_great_slash）。</summary>
        public static void PlayNailArtGreatSlash()
        {
            PlayOnce(_nailArtGreatSlashWav);
        }

        /// <summary>骨钉技艺：蓄力开始音效（一次性）。</summary>
        public static void PlayNailArtChargeInitiate()
        {
            PlayOnce(_nailArtChargeInitiateWav);
        }

        /// <summary>骨钉技艺：蓄满完成音效（一次性）。</summary>
        public static void PlayNailArtChargeComplete()
        {
            PlayOnce(_nailArtChargeCompleteWav);
        }

        /// <summary>骨钉技艺：蓄满后循环音效，直到释放强力劈砍/取消。</summary>
        public static void PlayNailArtChargeLoop()
        {
            if (_mixer == null || _nailArtChargeLoopWav == null)
            {
                return;
            }
            _mixer.Play(_nailArtChargeLoopWav, NailArtChargeGroup, true);
        }

        public static void StopNailArtChargeLoop()
        {
            if (_mixer != null)
            {
                _mixer.StopGroup(NailArtChargeGroup);
            }
        }

        /// <summary>骨钉技艺·旋风劈砍旋转音效（施放至停止，一次性播放）。</summary>
        public static void PlayNailArtCyclone()
        {
            PlayOnce(_nailArtCycloneWav);
        }

        /// <summary>梦钉短蓄力音：按下梦钉键开始播放。</summary>
        public static void PlayDreamNailCharge()
        {
            if (_mixer == null || _dreamNailChargeWav == null)
            {
                return;
            }
            _mixer.Play(_dreamNailChargeWav, DreamNailChargeGroup, false);
        }

        /// <summary>梦钉短蓄力音：松开梦钉键时快速淡出。</summary>
        public static void StopDreamNailChargeFade()
        {
            if (_mixer != null)
            {
                _mixer.StopGroupFade(DreamNailChargeGroup, 0.1f);
            }
        }

        /// <summary>梦钉命中怪物音效（hero_dream_nail_slash_only）。</summary>
        public static void PlayDreamNailSlash()
        {
            if (_mixer == null || _dreamNailSlashWav == null)
            {
                return;
            }
            _mixer.Play(_dreamNailSlashWav, DreamNailSlashGroup, false);
        }

        /// <summary>停止梦钉抽出音效（受伤终止时）。</summary>
        public static void StopDreamNailSlash()
        {
            if (_mixer != null)
            {
                _mixer.StopGroup(DreamNailSlashGroup);
            }
        }

        private static void PlayOnce(byte[] wav)
        {
            if (_mixer == null || wav == null)
            {
                return;
            }
            _mixer.Play(wav, 0, false);
        }

        /// <summary>播放任意 wav 字节（一次性），供护符 UI 等复用同一混音器通道。</summary>
        public static void PlayWavOnce(byte[] wav)
        {
            PlayOnce(wav);
        }

        /// <summary>从插件素材目录加载小格林音效（外部 wav，便于替换无需重编译）。</summary>
        public static void LoadGrimmSounds()
        {
            try
            {
                string dir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk",
                    "sheets", "grimm", "AudioClip");
                string idlePath = Path.Combine(dir, "Grimmbat_idle.wav");
                string y4 = Path.Combine(dir, "Grimmbat_attack_yelp_04.wav");
                string y5 = Path.Combine(dir, "Grimmbat_attack_yelp_05.wav");
                if (File.Exists(idlePath))
                {
                    _grimmIdleWav = File.ReadAllBytes(idlePath);
                }
                if (File.Exists(y4))
                {
                    _grimmYelp4Wav = File.ReadAllBytes(y4);
                }
                if (File.Exists(y5))
                {
                    _grimmYelp5Wav = File.ReadAllBytes(y5);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>小格林待机循环音（不攻击时循环）。</summary>
        public static void PlayGrimmIdleLoop()
        {
            if (_mixer == null || _grimmIdleWav == null)
            {
                return;
            }
            _mixer.Play(_grimmIdleWav, GrimmIdleGroup, true);
        }

        /// <summary>停止小格林待机循环音。</summary>
        public static void StopGrimmIdleLoop()
        {
            if (_mixer == null)
            {
                return;
            }
            _mixer.StopGroup(GrimmIdleGroup);
        }

        /// <summary>小格林攻击音效（yelp_04 / yelp_05 随机）。</summary>
        public static void PlayGrimmAttackYelp()
        {
            byte[] wav = (_flukeRnd.Next(2) == 0) ? _grimmYelp4Wav : _grimmYelp5Wav;
            PlayOnce(wav);
        }

        /// <summary>从插件素材目录加载无忧旋律音效（外部 wav，便于替换无需重编译）。</summary>
        public static void LoadTuneSound()
        {
            try
            {
                string path = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk",
                    "sheets", "charms", "Audio", "tune.wav");
                if (File.Exists(path))
                {
                    // 去掉开头静音（原素材约 0.43s 前导空白，导致听起来像受击动画后才播放），
                    // 并把音量提升到 2 倍（钳制防爆音）。
                    _tuneWav = ScaleWav(TrimLeadingSilence(File.ReadAllBytes(path)), 2f);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 裁掉 16bit PCM WAV 开头音量低于阈值的静音帧（最多 0.5s），返回新的 WAV 字节。
        /// 找不到明显声音或格式不支持时原样返回。
        /// </summary>
        private static byte[] TrimLeadingSilence(byte[] wav, short threshold = 16)
        {
            try
            {
                if (wav == null || wav.Length < 44 ||
                    Encoding.ASCII.GetString(wav, 0, 4) != "RIFF")
                {
                    return wav;
                }
                int p = 12;
                int dataOff = -1;
                int dataLen = 0;
                int channels = 1;
                int bits = 16;
                while (p + 8 <= wav.Length)
                {
                    string id = Encoding.ASCII.GetString(wav, p, 4);
                    int sz = BitConverter.ToInt32(wav, p + 4);
                    if (id == "fmt " && sz >= 16)
                    {
                        channels = BitConverter.ToUInt16(wav, p + 10);
                        bits = BitConverter.ToUInt16(wav, p + 22);
                    }
                    else if (id == "data")
                    {
                        dataOff = p + 8;
                        dataLen = Math.Min(sz, wav.Length - dataOff);
                        break;
                    }
                    p += 8 + sz + (sz & 1);
                }
                int bps = bits / 8;
                if (dataOff < 0 || dataLen < 4 || bps < 1 || channels < 1)
                {
                    return wav;
                }
                int frameBytes = bps * channels;
                int frames = dataLen / frameBytes;
                int limit = Math.Min(frames, (int)(44100 * 0.5)); // 最多裁 0.5s 前导静音
                int skipFrames = 0;
                for (int f = 0; f < limit; f++)
                {
                    bool loud = false;
                    for (int c = 0; c < channels; c++)
                    {
                        int off = dataOff + (f * channels + c) * bps;
                        if (off + 1 >= wav.Length)
                        {
                            break;
                        }
                        short s = (short)(wav[off] | (wav[off + 1] << 8));
                        if (Math.Abs((int)s) > threshold)
                        {
                            loud = true;
                            break;
                        }
                    }
                    if (loud)
                    {
                        skipFrames = Math.Max(0, f - 2); // 保留约 2 帧（≈0.05ms 内）的攻击起始
                        break;
                    }
                }
                if (skipFrames <= 0)
                {
                    return wav;
                }
                int skipBytes = skipFrames * frameBytes;
                byte[] dst = new byte[wav.Length - skipBytes];
                Buffer.BlockCopy(wav, 0, dst, 0, dataOff);
                Buffer.BlockCopy(wav, dataOff + skipBytes, dst, dataOff,
                    wav.Length - dataOff - skipBytes);
                // 更新 data 块大小与 RIFF 大小
                Buffer.BlockCopy(BitConverter.GetBytes(dataLen - skipBytes), 0, dst, dataOff - 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(dst.Length - 8), 0, dst, 4, 4);
                return dst;
            }
            catch (Exception)
            {
                return wav;
            }
        }

        /// <summary>无忧旋律免伤成功时播放守护之歌音效。</summary>
        public static void PlayTune()
        {
            PlayOnce(_tuneWav);
        }

        /// <summary>从插件素材目录加载虚空解放音效（外部 wav，便于替换无需重编译）。</summary>
        public static void LoadVoidLiberationSounds()
        {
            try
            {
                string dir = Path.Combine(Paths.PluginPath, "KnightInCradle", "assets", "hk",
                    "sheets", "void_liberation");
                // 素材按子目录组织：Audio（兼容旧平铺布局则直接读文件夹根）
                string audioDir = Path.Combine(dir, "Audio");
                string charge = Path.Combine(audioDir, "hero_dream_nail_short_charge.wav");
                string challenge = Path.Combine(audioDir, "radiance_challenge.wav");
                string knockDown = Path.Combine(audioDir, "radiance_knock_down.wav");
                if (!File.Exists(charge))
                {
                    charge = Path.Combine(dir, "hero_dream_nail_short_charge.wav");
                }
                if (!File.Exists(challenge))
                {
                    challenge = Path.Combine(dir, "radiance_challenge.wav");
                }
                if (!File.Exists(knockDown))
                {
                    knockDown = Path.Combine(dir, "radiance_knock_down.wav");
                }
                if (File.Exists(charge))
                {
                    _voidChargeWav = File.ReadAllBytes(charge);
                }
                if (File.Exists(challenge))
                {
                    _voidChallengeWav = File.ReadAllBytes(challenge);
                }
                if (File.Exists(knockDown))
                {
                    _voidKnockDownWav = File.ReadAllBytes(knockDown);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>虚空解放·前摇：蓄力音 + 挑战音同时播放。</summary>
        public static void PlayVoidCharge()
        {
            if (_mixer == null)
            {
                return;
            }
            if (_voidChargeWav != null)
            {
                _mixer.Play(_voidChargeWav, 0, false);
            }
            if (_voidChallengeWav != null)
            {
                _mixer.Play(_voidChallengeWav, VoidChallengeGroup, false);
            }
        }

        /// <summary>虚空解放·出伤：黑屏后的击倒音效。</summary>
        public static void PlayVoidStrike()
        {
            PlayOnce(_voidKnockDownWav);
        }

        private static byte[] ReadResource(string resourceName)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (s == null)
                    {
                        return null;
                    }
                    var bytes = new byte[s.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = s.Read(bytes, read, bytes.Length - read);
                        if (n <= 0)
                        {
                            break;
                        }
                        read += n;
                    }
                    return bytes;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 把 16bit PCM WAV 的所有采样乘以 volume（可大于 1 放大），并做钳制防止爆音。
        /// </summary>
        private static byte[] ScaleWav(byte[] src, float volume)
        {
            if (src == null || src.Length < 44)
            {
                return src;
            }
            byte[] dst = (byte[])src.Clone();
            int dataSize = BitConverter.ToInt32(dst, 40);
            int end = Math.Min(44 + dataSize, dst.Length);
            for (int i = 44; i + 1 < end; i += 2)
            {
                short sample = BitConverter.ToInt16(dst, i);
                int v = (int)(sample * volume);
                if (v > short.MaxValue)
                {
                    v = short.MaxValue;
                }
                if (v < short.MinValue)
                {
                    v = short.MinValue;
                }
                dst[i] = (byte)(v & 0xFF);
                dst[i + 1] = (byte)((v >> 8) & 0xFF);
            }
            return dst;
        }
    }

    /// <summary>
    /// 极简 waveOut 混音器：单输出设备 + 后台线程混音。
    /// 支持多段音效叠加播放、循环播放、按分组停止；
    /// 输出固定 44100Hz / 16bit / 双声道，所有 WAV 先解码重采样到该格式。
    /// </summary>
    internal sealed class WavMixer
    {
        private const int OutRate = 44100;
        private const int FramesPerBuf = 1024;
        private const int BufCount = 3;
        private const uint WAVE_MAPPER = 0xFFFFFFFF;
        private const uint WHDR_DONE = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveOutOpen(out IntPtr hwo, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveOutClose(IntPtr hwo);

        private sealed class Snd
        {
            public float[] Data; // 44100Hz 双声道 float（帧 × 2）
            public int FrameCount;
            public int Pos;
            public int Group;
            public bool Loop;
            public bool Fading;
            public float FadeVol = 1f;
            public float FadeStep;
        }

        private readonly object _lock = new object();
        private readonly List<Snd> _active = new List<Snd>();
        private IntPtr _h;
        private IntPtr _hdrMem;
        private IntPtr[] _pins;
        private bool[] _written;
        private Thread _th;
        private volatile bool _run;

        public void Start()
        {
            var fmt = new WAVEFORMATEX();
            fmt.wFormatTag = 1;
            fmt.nChannels = 2;
            fmt.nSamplesPerSec = OutRate;
            fmt.wBitsPerSample = 16;
            fmt.nBlockAlign = 4;
            fmt.nAvgBytesPerSec = (uint)(OutRate * 4);
            if (waveOutOpen(out _h, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0)
            {
                return;
            }
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            _hdrMem = Marshal.AllocHGlobal(hdrSize * BufCount);
            _pins = new IntPtr[BufCount];
            _written = new bool[BufCount];
            for (int i = 0; i < BufCount; i++)
            {
                _pins[i] = Marshal.AllocHGlobal(FramesPerBuf * 4);
                var hdr = new WAVEHDR();
                hdr.lpData = _pins[i];
                hdr.dwBufferLength = (uint)(FramesPerBuf * 4);
                Marshal.StructureToPtr(hdr, IntPtr.Add(_hdrMem, i * hdrSize), false);
            }
            _run = true;
            _th = new Thread(MixLoop) { IsBackground = true };
            _th.Start();
        }

        public void Play(byte[] wav, int group, bool loop)
        {
            float[] data = DecodeWav(wav);
            if (data == null || data.Length < 128)
            {
                return;
            }
            lock (_lock)
            {
                _active.Add(new Snd
                {
                    Data = data,
                    FrameCount = data.Length / 2,
                    Group = group,
                    Loop = loop
                });
            }
        }

        public void StopGroup(int group)
        {
            lock (_lock)
            {
                for (int i = _active.Count - 1; i >= 0; i--)
                {
                    if (_active[i].Group == group)
                    {
                        _active.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>停止某分组并快速淡出（用于梦钉短蓄力音效中途松开）。</summary>
        public void StopGroupFade(int group, float fadeSeconds)
        {
            lock (_lock)
            {
                for (int i = 0; i < _active.Count; i++)
                {
                    Snd s = _active[i];
                    if (s.Group == group && !s.Fading)
                    {
                        s.Fading = true;
                        s.FadeVol = 1f;
                        float fadeSec = fadeSeconds < 0.01f ? 0.01f : fadeSeconds;
                        s.FadeStep = 1f / (OutRate * fadeSec);
                    }
                }
            }
        }

        private void MixLoop()
        {
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            int idx = 0;
            while (_run)
            {
                IntPtr hp = IntPtr.Add(_hdrMem, idx * hdrSize);
                WAVEHDR hdr = (WAVEHDR)Marshal.PtrToStructure(hp, typeof(WAVEHDR));
                if (_written[idx] && (hdr.dwFlags & WHDR_DONE) == 0)
                {
                    Thread.Sleep(2);
                    continue;
                }
                if (_written[idx])
                {
                    waveOutUnprepareHeader(_h, hp, (uint)hdrSize);
                    _written[idx] = false;
                }
                Fill(_pins[idx]);
                waveOutPrepareHeader(_h, hp, (uint)hdrSize);
                waveOutWrite(_h, hp, (uint)hdrSize);
                _written[idx] = true;
                idx = (idx + 1) % BufCount;
            }
            for (int i = 0; i < BufCount; i++)
            {
                if (_written[i])
                {
                    waveOutUnprepareHeader(_h, IntPtr.Add(_hdrMem, i * hdrSize), (uint)hdrSize);
                }
                if (_pins[i] != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_pins[i]);
                }
            }
            if (_hdrMem != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_hdrMem);
            }
            waveOutClose(_h);
        }

        private void Fill(IntPtr dst)
        {
            var buf = new byte[FramesPerBuf * 4];
            lock (_lock)
            {
                for (int f = 0; f < FramesPerBuf; f++)
                {
                    float l = 0f;
                    float r = 0f;
                    for (int i = _active.Count - 1; i >= 0; i--)
                    {
                        Snd s = _active[i];
                        if (s.Pos >= s.FrameCount)
                        {
                            if (s.Loop)
                            {
                                s.Pos = 0;
                            }
                            else
                            {
                                _active.RemoveAt(i);
                                continue;
                            }
                        }
                        float vol = s.Fading ? s.FadeVol : 1f;
                        l += s.Data[s.Pos * 2] * vol;
                        r += s.Data[s.Pos * 2 + 1] * vol;
                        s.Pos++;
                        if (s.Fading)
                        {
                            s.FadeVol -= s.FadeStep;
                            if (s.FadeVol <= 0f)
                            {
                                _active.RemoveAt(i);
                                continue;
                            }
                        }
                    }
                    WriteSample(buf, f * 4, l);
                    WriteSample(buf, f * 4 + 2, r);
                }
            }
            Marshal.Copy(buf, 0, dst, buf.Length);
        }

        private static void WriteSample(byte[] buf, int off, float v)
        {
            if (v > 1f)
            {
                v = 1f;
            }
            if (v < -1f)
            {
                v = -1f;
            }
            int s = (int)(v * 32767f);
            buf[off] = (byte)(s & 0xFF);
            buf[off + 1] = (byte)((s >> 8) & 0xFF);
        }

        /// <summary>解析 PCM16 WAV 并转成 44100Hz 双声道 float（单声道扩双、异采样率重采样）。</summary>
        private static float[] DecodeWav(byte[] wav)
        {
            try
            {
                if (wav == null || wav.Length < 44 ||
                    Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" ||
                    Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
                {
                    return null;
                }
                ushort channels = 1;
                int rate = OutRate;
                ushort bits = 16;
                int p = 12;
                while (p + 8 <= wav.Length)
                {
                    string id = Encoding.ASCII.GetString(wav, p, 4);
                    int sz = BitConverter.ToInt32(wav, p + 4);
                    if (id == "fmt ")
                    {
                        if (sz >= 16)
                        {
                            ushort fmtTag = BitConverter.ToUInt16(wav, p + 8);
                            if (fmtTag != 1)
                            {
                                return null; // 仅支持 PCM
                            }
                            channels = BitConverter.ToUInt16(wav, p + 10);
                            rate = BitConverter.ToInt32(wav, p + 12);
                            bits = BitConverter.ToUInt16(wav, p + 22);
                        }
                    }
                    else if (id == "data")
                    {
                        int dataOff = p + 8;
                        int dataLen = Math.Min(sz, wav.Length - dataOff);
                        int bytesPerSample = bits / 8;
                        if (bytesPerSample < 1 || channels < 1 || rate < 1)
                        {
                            return null;
                        }
                        int sampleCount = dataLen / bytesPerSample;
                        int srcFrames = sampleCount / channels;
                        if (srcFrames < 1)
                        {
                            return null;
                        }
                        float[] src = new float[srcFrames * channels];
                        for (int i = 0; i < srcFrames * channels; i++)
                        {
                            int off = dataOff + i * bytesPerSample;
                            if (off + 1 >= wav.Length)
                            {
                                break;
                            }
                            short s16 = (short)(wav[off] | (wav[off + 1] << 8));
                            src[i] = s16 / 32768f;
                        }
                        int outFrames = Math.Max(1, (int)Math.Ceiling(srcFrames * (double)OutRate / rate));
                        float[] dst = new float[outFrames * 2];
                        double step = (double)srcFrames / outFrames;
                        for (int f = 0; f < outFrames; f++)
                        {
                            double pos = f * step;
                            int i0 = (int)pos;
                            int i1 = Math.Min(i0 + 1, srcFrames - 1);
                            float t = (float)(pos - i0);
                            if (channels == 1)
                            {
                                float s = src[i0] + (src[i1] - src[i0]) * t;
                                dst[f * 2] = s;
                                dst[f * 2 + 1] = s;
                            }
                            else
                            {
                                dst[f * 2] = src[i0 * 2] + (src[i1 * 2] - src[i0 * 2]) * t;
                                dst[f * 2 + 1] = src[i0 * 2 + 1] + (src[i1 * 2 + 1] - src[i0 * 2 + 1]) * t;
                            }
                        }
                        return dst;
                    }
                    p += 8 + sz + (sz & 1);
                }
            }
            catch (Exception)
            {
            }
            return null;
        }
    }
}
