using System;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using evt;
using HarmonyLib;
using m2d;
using nel;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using XX;
using KnightInCradle.CharmUi;

namespace KnightInCradle
{
    public class KnightInCradleBehaviour : MonoBehaviour
    {
        /// <summary>
        /// 构建标记：每次部署时手动更新，日志 `[KIC][补丁] build=…` 会打印；
        /// 配合后面的 `dll=路径 (文件时间)` 可以立刻确认游戏实际加载的是哪一份 DLL。
        /// </summary>
        internal const string SelfBuildTag = "2026-09-22.22";

        private static bool _harmonyApplied;
        private static bool _seriousInitApplied; // 启动时是否已应用过一次布局（防止残留居中布局）
        private static int _lastCheckFrame = -1;
        private static bool _knightMode;
        private static readonly FieldInfo SerBitsField = AccessTools.Field(typeof(M2Ser), "ser_bits");
        private static readonly FieldInfo NoelRtkField = AccessTools.Field(typeof(M2PxlAnimatorRT), "RTkt");
        private static M2Mover.DRAW_ORDER _noelRtkOrder; // 切回时重建票据用
        /// <summary>“。”键（句号）切换：认真模式——隐藏左侧诺艾尔立绘，游戏画面居中全屏。</summary>
        public static bool SeriousMode;
        private CharmUiOnGuiLayer _charmUiLayer;
        private CharmUiController _charmUiController;

        private static readonly FieldInfo AnmField = AccessTools.Field(typeof(PrAnimator), "Anm");
        private static readonly FieldInfo ShadowField = AccessTools.Field(typeof(M2PxlAnimator), "EfShadow");
        private static readonly FieldInfo CamMvCenterField = AccessTools.Field(typeof(M2Camera), "MvCenter");
        // 认真模式：UIBase.redraw_flag_ 私有，用反射强制“左侧布局重算 + 立绘位置重算”
        private static readonly FieldInfo UiRedrawFlagField = AccessTools.Field(typeof(UIBase), "redraw_flag_");
        // 强制镜头标志：M2Camera.FocusTo_ 非空且 active 时，镜头正被地图焦点拉走（剧情/战斗强制转镜）
        private static readonly FieldInfo CamFocusToField = AccessTools.Field(typeof(M2Camera), "FocusTo_");
        // 低血量屏幕黑边滤镜（径向渐变：中心透明、四周黑色）
        private static Texture2D _vignetteTex;
        // 亡者之怒屏幕红边滤镜（矩形边框：适配屏幕比例、四角直角）
        private static Texture2D _furyVignetteTex;
        private static int _furyVignetteW = -1;
        private static int _furyVignetteH = -1;
        // 凝聚回血成功的屏幕闪光（径向渐变：中心透明、四周白色）
        private static Texture2D _flashTex;
        // 切出小骑士时，把诺艾尔的碰撞体改成小骑士判定箱（记录原尺寸，切回时恢复）
        private static float _noelOrigSizex;
        private static float _noelOrigSizey;
        private static bool _noelResized;
        private float _diagTimer;
        // 进入骑士模式时的诺艾尔真实 HP/MP 快照（防 getter 劫持被游戏内部逻辑误写）
        private static int _noelSnapshotHp = -1;
        private static int _noelSnapshotMaxHp = -1;
        private static int _noelSnapshotMp = -1;
        private static int _noelSnapshotMaxMp = -1;
        // M2Attackable 的 HP/MP 字段（protected int，反射直读，绕过 CombatGuard 的 getter 劫持）
        private static readonly FieldInfo NoelHpField = AccessTools.Field(typeof(M2Attackable), "hp");
        private static readonly FieldInfo NoelMaxHpField = AccessTools.Field(typeof(M2Attackable), "maxhp");
        private static readonly FieldInfo NoelMpField = AccessTools.Field(typeof(M2Attackable), "mp");
        private static readonly FieldInfo NoelMaxMpField = AccessTools.Field(typeof(M2Attackable), "maxmp");

        private void Awake()
        {
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        }

        private void Start()
        {
            ApplyHarmony();
        }

        private void Update()
        {
            _diagTimer += Time.unscaledDeltaTime;
            MultiplayerCompat.Tick(); // 联机模组兼容补丁（装没装 Kaleidoscopic 都空转）

            if (!_harmonyApplied && _diagTimer > 1f)
            {
                ApplyHarmony();
            }

            TryToggle();

            // 启动初始化：强制应用“非认真模式”布局（立绘在左、HUD 偏右）。
            // 否则若上次会话切过认真模式，游戏会载入 uipic_lr=NONE（居中），
            // 而 SeriousMode 标志默认 false，导致两边不一致。
            if (!_seriousInitApplied && UIBase.Instance != null)
            {
                _seriousInitApplied = true;
                // 认真模式是全局开关：从配置读取上次状态（默认 false=显示立绘）
                SeriousMode = KnightInCradlePlugin.SeriousModePersistConfig != null &&
                    KnightInCradlePlugin.SeriousModePersistConfig.Value;
                ApplySeriousMode();
            }

            // “。”键（KeyCode.Period，可配置）：认真模式开关（立绘位置 L ↔ NONE，游戏自带布局逻辑会隐藏立绘并居中）
            if (KeyConfig.GetPressed(KnightInCradlePlugin.SeriousModeKey, KeyCode.Period))
            {
                SeriousMode = !SeriousMode;
                ApplySeriousMode();
                // 全局持久化：写入配置，进游戏/读档后保持
                if (KnightInCradlePlugin.SeriousModePersistConfig != null)
                {
                    KnightInCradlePlugin.SeriousModePersistConfig.Value = SeriousMode;
                }
            }
            // 认真模式期间：每帧保持立绘隐藏（防止游戏事件/状态切换时重新显示）
            if (SeriousMode)
            {
                KeepSeriousModeApplied();
            }

            // “O”键（可配置）：护符 UI 开关（避开 KeyConfig 屏蔽，界面打开时也能用 O 关闭）
            KeyCode charmUiToggleKey = KeyConfig.Parse(
                KnightInCradlePlugin.CharmUiKey != null ? KnightInCradlePlugin.CharmUiKey.Value : null,
                KeyCode.O);
            if (UnityEngine.Input.GetKeyDown(charmUiToggleKey))
            {
                ToggleCharmUi();
            }

            // 护符 UI 每帧驱动：刷新坐姿状态；编辑对象离开对应模式时自动关闭
            // （小骑士护符只在骑士模式显示，诺艾尔护符只在诺艾尔模式显示）
            if (_charmUiController != null)
            {
                CharmOwner uiOwner = _charmUiController.Owner;
                PRNoel prUi = GetPr();
                bool ownerValid = uiOwner == CharmOwner.Knight
                    ? (_knightMode && KnightEntity.Instance != null)
                    : (!_knightMode && prUi != null);
                if (!ownerValid)
                {
                    if (_charmUiController.IsOpen)
                    {
                        _charmUiController.Close();
                        if (_charmUiLayer != null)
                        {
                            _charmUiLayer.SetVisible(false);
                        }
                    }
                }
                else
                {
                    _charmUiController.RefreshSitting(uiOwner == CharmOwner.Knight
                        ? KnightEntity.Instance.IsSitting
                        : (prUi != null && prUi.isBenchState()));
                    _charmUiController.Update();
                }
            }

            // 诺艾尔模式：护符效果里"每帧维护"的部分（目前是护符2 蜂群集结的两件事）。
            // 未装备护符时这些调用都是立即返回的空操作。
            if (!_knightMode)
            {
                TickNoelCharmEffects();
            }

            if (_knightMode)
            {
                ForceNoelGone();
                // 健康态周期兜底：若白名单被绕过导致状态位残留，清回无状态（每 60 帧一次）
                if (Time.frameCount % 60 == 0)
                {
                    try
                    {
                        PRNoel prH = GetPr();
                        if (prH != null && prH.Ser != null && SerBitsField != null)
                        {
                            ulong bits = (ulong)SerBitsField.GetValue(prH.Ser);
                            if (bits != 0UL)
                            {
                                prH.Ser.clear();
                            }
                        }
                        if (prH != null)
                        {
                            // 确保状态注入保持关闭（负面状态不会再次注入）
                            if (prH.SttInjector != null && prH.SttInjector.enabled)
                            {
                                prH.SttInjector.enabled = false;
                            }
                            HideNoelO2Gauge();
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                // 每帧确保动画白名单监听器已挂上（诺艾尔动画器可能在换图/剧情中被重建）
                try
                {
                    PRNoel prWl = GetPr();
                    if (prWl != null)
                    {
                        RegisterNoelPoseWhitelist(prWl);
                    }
                }
                catch (Exception)
                {
                }
                // 小骑士模式下 ESC 随时打开游戏菜单：骑士模式会卡住 PR.runUi 的输入门控，
                // 这里直接走 AIC 原生的 menu_open = OPEN 通道（菜单已开时由游戏 UI 自身处理关闭）
                TryOpenGameMenuWithEsc();
                // 小骑士模式下 M 键打开地图（复刻 PR.runUi 的 isMapPD 判定）：
                // 坐在长椅上时借此选择快速旅行传送；菜单已开时由游戏 UI 自身处理
                TryOpenMapWithM();
                // 诺艾尔身体同步到小骑士位置：镜头（跟随诺艾尔）因此跟随小骑士
                bool transferring = M2DBase.Instance != null && M2DBase.Instance.transferring_game_stopping;
                // 每帧把诺艾尔真实 HP/MP 字段恢复为骑士模式快照：
                // CombatGuard 把诺艾尔的 get_maxmp()/get_mp()/get_hp()/get_maxhp() 劫持为
                // 小骑士数值，游戏内部逻辑（如 M2PrSkill.SkillApplyMem.Apply → ApplySkillFixParameter）
                // 会把读到的骑士数值写回诺艾尔字段，造成“放法术/受伤后诺艾尔 MP/HP 被污染”。
                // 转房/重定位阶段（剧情脚本可能合法改值）跳过恢复。
                if (!transferring &&
                    (KnightEntity.Instance == null ||
                     (!KnightEntity.Instance.PendingRepositionActive &&
                      !KnightEntity.Instance.IsRepositioning)))
                {
                    PRNoel prSnap = GetPr();
                    if (prSnap != null)
                    {
                        RestoreNoelSnapshot(prSnap);
                    }
                }
                // 过图跟随阶段：必须保持诺艾尔物理运行（AIC 的进门强制位移要推动她），
                // 由 KnightEntity 的 _pendingRepositionActive 标志告知本阶段。
                if (KnightEntity.Instance != null && KnightEntity.Instance.PendingRepositionActive)
                {
                    try
                    {
                        PRNoel prR = GetPr();
                        if (prR != null)
                        {
                            M2Phys phyR = prR.getPhysic();
                            if (phyR != null && phyR.isPausing())
                            {
                                phyR.Resume();
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                if (KnightEntity.Instance != null &&
                    KnightEntity.Instance.IsRepositioning == false &&
                    !transferring)
                {
                    PRNoel pr = GetPr();
                    if (pr != null)
                    {
                        SyncNoelToKnight(pr);
                        // 镜头跟随速度：setTo 内部会提到 4（偏快），这里压到 3——比诺艾尔正常(2)略大，跟得住骑士又不激进
                        M2DBase m2d = M2DBase.Instance;
                        if (m2d != null && m2d.Cam != null)
                        {
                            // MvCenter 是 protected，走反射读取当前镜头跟随的移动体
                            object mvCenter = CamMvCenterField != null
                                ? CamMvCenterField.GetValue(m2d.Cam)
                                : null;
                            if (ReferenceEquals(mvCenter, pr))
                            {
                                // 强制镜头判定：FocusTo_ 非空且 active 时，镜头正被地图焦点
                                // （M2LpCamFocus，如剧情/战斗强制转镜）拉向指定坐标。
                                // 此时绝不干预，让 AIC 原版镜头完全接管，避免
                                // “强制镜头位置 ↔ 小骑士位置”来回横跳。
                                // FocusTo_ 是 private，走反射；active 是 DRect 上的 public 字段。
                                bool forcedCam = false;
                                object focusObj = CamFocusToField != null
                                    ? CamFocusToField.GetValue(m2d.Cam)
                                    : null;
                                if (focusObj is M2LpCamFocus focus)
                                {
                                    forcedCam = focus.active;
                                }
                                if (!forcedCam)
                                {
                                    float clen = m2d.Cam.CLEN;
                                    float camX = clen > 0f ? m2d.Cam.x / clen : 0f;
                                    float camY = clen > 0f ? m2d.Cam.y / clen : 0f;
                                    float dist = Mathf.Max(
                                        Mathf.Abs(camX - KnightEntity.Instance.X),
                                        Mathf.Abs(camY - KnightEntity.Instance.Y));
                                    // 普通跟随比诺艾尔略快
                                    float camSpeed = 4f;
                                    // 超级冲刺：单独使用超冲专用快速镜头，速度接近冲刺本身，
                                    // 保证镜头跟得上（强制镜头期间上面的 forcedCam 分支已接管，不会打架）
                                    if (KnightEntity.Instance.IsSuperDashing)
                                    {
                                        camSpeed = 32f;
                                    }
                                    // 距离越远越提速（平滑追回，不瞬移）：镜头每帧按剩余距离的比例
                                    // 移动，提高 cam_walk_speed 即加快追赶速度，但轨迹始终连续
                                    else if (dist > 4f)
                                    {
                                        camSpeed = Mathf.Clamp(dist * 1.6f, 4f, 24f);
                                    }
                                    m2d.Cam.cam_walk_speed_x = camSpeed;
                                    m2d.Cam.cam_walk_speed_y = camSpeed;
                                    // 极端兜底：仅当镜头完全丢失（>24格，跨越两屏以上）时才一次性拉回，
                                    // 正常玩法（含强制镜头结束后的回追）不会触发，杜绝生硬瞬移
                                    if (dist > 24f)
                                    {
                                        m2d.Cam.setTo(KnightEntity.Instance.X, KnightEntity.Instance.Y);
                                    }
                                }
                            }
                        }
                        // 每帧强制隐藏诺艾尔模型：遍历她身上所有 M2PxlAnimatorRT（毒气可能用独立渲染器画她）
                        M2PxlAnimatorRT[] anms = pr.GetComponentsInChildren<M2PxlAnimatorRT>(true);
                        for (int ai = 0; ai < anms.Length; ai++)
                        {
                            if (anms[ai] != null)
                            {
                                if (anms[ai].alpha > 0f)
                                {
                                    anms[ai].alpha = 0f;
                                }
                                // 强制重建网格：雾效果可能让缓存网格停留在不透明状态
                                anms[ai].need_fine = true;
                                anms[ai].fineCurrentFrameMeshManual();
                            }
                        }
                        // 兜底：禁用并记录诺艾尔身上所有标准渲染器（独立网格可能不走 M2PxlAnimatorRT）
                        HideNoelRenderTicket(pr, true);
                        DisableNoelRenderers(pr);
                    }
                }
            }
        }

        /// <summary>
        /// 护符 UI 开关。两套护符互相独立、互不影响：
        ///   - 小骑士模式 → 编辑小骑士的护符（原有行为）
        ///   - 诺艾尔模式 → 编辑诺艾尔的护符（"诺艾尔的护符"第一部分）
        /// 两边规则一致：随时可以打开，但只有坐在长椅上才能装配/卸下。
        /// 首次打开时加载布局并创建控制器，之后切换显示/隐藏。
        /// </summary>
        private void ToggleCharmUi()
        {
            try
            {
                // 已打开 → 关闭
                if (_charmUiController != null && _charmUiController.IsOpen)
                {
                    // 关闭不做动画：按 O 立即隐藏
                    _charmUiController.CloseAndHide();
                    return;
                }

                // 归属 + 坐姿来源：骑士模式取小骑士，诺艾尔模式取诺艾尔本体
                CharmOwner owner;
                bool sitting;
                if (_knightMode)
                {
                    KnightEntity k = KnightEntity.Instance;
                    if (k == null || k.IsDead)
                    {
                        return; // 小骑士死亡/未生成时不打开
                    }
                    owner = CharmOwner.Knight;
                    sitting = k.IsSitting;
                }
                else
                {
                    PRNoel pr = GetPr();
                    if (pr == null || !pr.is_alive || pr.get_hp() <= 0)
                    {
                        return; // 诺艾尔未生成/死亡时不打开
                    }
                    owner = CharmOwner.Noel;
                    // 诺艾尔"坐着"= AIC 原生长椅状态（BENCH / BENCH_LOADAFTER / BENCH_ONNIE）
                    sitting = pr.isBenchState();
                }

                if (!EnsureCharmUiCreated())
                {
                    return;
                }
                _charmUiController.SetOwner(owner);
                _charmUiController.RefreshSitting(sitting);
                _charmUiController.Open();
                _charmUiLayer.SetVisible(true);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>首次需要时创建护符 UI 渲染层与控制器（两套护符共用同一套 UI）。</summary>
        private bool EnsureCharmUiCreated()
        {
            if (_charmUiLayer != null && _charmUiController != null)
            {
                return true;
            }
            string dir = Path.Combine(BepInEx.Paths.PluginPath, "KnightInCradle", "charm_ui");
            _charmUiLayer = CharmUiOnGuiLayer.Create(gameObject, dir);
            if (_charmUiLayer == null)
            {
                return false;
            }
            _charmUiController = new CharmUiController(_charmUiLayer);
            _charmUiLayer.Controller = _charmUiController;
            return true;
        }

        /// <summary>诺艾尔模式换图缓存：只在换图时重算蜂巢标记，避免每帧重置"攻击后敌对"。</summary>
        private static Map2d _noelCharmMap;

        /// <summary>
        /// 诺艾尔模式下"每帧维护"的护符效果，与骑士模式里 KnightEntity.Update 调用的那几个一一对应：
        /// ① 换图时重算蜂巢房间标记（骑士模式由 KnightEntity 的换图分支调 UpdateHiveRoom）；
        /// ② 护符2 蜂群集结：蜂巢中立期每帧清除魔物的锁定目标；
        /// ③ 护符2 蜂群集结：按诺艾尔坐标做 3 格内自动拾取。
        /// ④ 护符3 坚硬外壳：次数血维护（装备/卸下时换算、装备期间钳制上限）。
        /// （魔力相关的那两项只服务小骑士侧，见 CharmEffects.CollectorManaGuardActive 的注释。）
        /// </summary>
        private static void TickNoelCharmEffects()
        {
            try
            {
                PRNoel pr = GetPr();
                if (pr == null || !pr.is_alive || pr.Mp == null)
                {
                    return;
                }
                if (!ReferenceEquals(pr.Mp, _noelCharmMap))
                {
                    _noelCharmMap = pr.Mp;
                    CharmEffects.UpdateHiveRoom(pr.Mp);
                }
                CharmEffects.TickNoelSturdyCharm(pr);
                CharmEffects.ClearHiveEnemyAim();
                CharmEffects.TickCollectorAutoPickup(pr.x, pr.mbottom);
            }
            catch (Exception)
            {
            }
        }

        private static void ApplySeriousMode()
        {
            try
            {
                UIBase ui = UIBase.Instance;
                if (ui != null)
                {
                    if (SeriousMode)
                    {
                        // 游戏内置“禁用诺艾尔区域”开关：隐藏立绘父节点（立绘+其背景面板），并清掉左侧背景
                        ui.FlgNoelAreaDisable.Add("__SERIOUS");
                    }
                    else
                    {
                        ui.FlgNoelAreaDisable.Rem("__SERIOUS");
                    }
                    ui.setDrawBgFlag(); // 强制重绘背景（NONE/禁用区域时不再画左侧立绘背景条）
                }
                CFGSP.uipic_lr = SeriousMode ? CFGSP.UIPIC_LR.NONE : CFGSP.UIPIC_LR.L;
                // 强制 HUD 重新布局（uipic_lr=NONE 时 HUD 不再右移，回到画面居中）
                if (UIStatus.Instance != null)
                {
                    UIStatus.Instance.need_reposit = true;
                }
                // 强制“左侧布局重算（uiResetLeftPos，镜头偏移归零）+ 立绘位置重算（finePictPos）”
                if (ui != null && UiRedrawFlagField != null)
                {
                    try
                    {
                        uint flag = (uint)UiRedrawFlagField.GetValue(ui);
                        UiRedrawFlagField.SetValue(ui, flag | 32768U | 1048576U);
                    }
                    catch (Exception)
                    {
                    }
                }
                // 立即把镜头/UI 偏移清零（认真模式下每帧也会保持）
                if (M2DBase.Instance != null)
                {
                    M2DBase.Instance.ui_shift_x = 0f;
                }
                if (ui != null)
                {
                    ui.ui_shift_x = 0f;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 读取存档/新游戏时调用：认真模式为全局开关，读档后保持当前状态并重新应用布局
        /// （防止 AIC 读档重建 UI 后布局回退）。
        /// </summary>
        public static void DisableSeriousModeOnLoad()
        {
            try
            {
                ApplySeriousMode();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>认真模式每帧保持生效：立绘/背景已由 FlgNoelAreaDisable 隐藏，这里确保镜头与 UI 偏移保持居中。</summary>
        private static void KeepSeriousModeApplied()
        {
            try
            {
                if (M2DBase.Instance != null)
                {
                    if (M2DBase.Instance.ui_shift_x != 0f)
                    {
                        M2DBase.Instance.ui_shift_x = 0f;
                    }
                }
                UIBase ui = UIBase.Instance;
                if (ui != null)
                {
                    if (ui.ui_shift_x != 0f)
                    {
                        ui.ui_shift_x = 0f;
                    }
                    if (!ui.FlgNoelAreaDisable.isActive())
                    {
                        ui.FlgNoelAreaDisable.Add("__SERIOUS");
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static void DisableNoelRenderers(PRNoel pr)
        {
            try
            {
                MeshRenderer[] mrs = pr.GetComponentsInChildren<MeshRenderer>(true);
                for (int i = 0; i < mrs.Length; i++)
                {
                    if (mrs[i] != null && mrs[i].enabled)
                    {
                        mrs[i].enabled = false;
                    }
                }
                SpriteRenderer[] srs = pr.GetComponentsInChildren<SpriteRenderer>(true);
                for (int i = 0; i < srs.Length; i++)
                {
                    if (srs[i] != null && srs[i].enabled)
                    {
                        srs[i].enabled = false;
                    }
                }
                SkinnedMeshRenderer[] sms = pr.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int i = 0; i < sms.Length; i++)
                {
                    if (sms[i] != null && sms[i].enabled)
                    {
                        sms[i].enabled = false;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private void LateUpdate()
        {
            if (_knightMode)
            {
                ForceNoelGone();
                // 渲染前兜底禁用诺艾尔渲染器：雾/地图效果可能在游戏 Update 阶段
                // 重新启用它们，LateUpdate 在渲染前再禁一次，消除“身后间歇显形”
                try
                {
                    PRNoel prLU = GetPr();
                    if (prLU != null)
                    {
                        M2PxlAnimatorRT[] anms = prLU.GetComponentsInChildren<M2PxlAnimatorRT>(true);
                        for (int ai = 0; ai < anms.Length; ai++)
                        {
                            if (anms[ai] != null && anms[ai].alpha > 0f)
                            {
                                anms[ai].alpha = 0f;
                            }
                        }
                        HideNoelRenderTicket(prLU, true);
                        DisableNoelRenderers(prLU);
                    }
                }
                catch (Exception)
                {
                }
                // 渲染/受击判定前的最后一次对齐：保证诺艾尔判定箱与小骑士判定箱同帧重合
                if (KnightEntity.Instance != null && KnightEntity.Instance.IsActive &&
                    !KnightEntity.Instance.IsRepositioning)
                {
                    PRNoel pr = GetPr();
                    if (pr != null)
                    {
                        SyncNoelToKnight(pr);
                    }
                }
            }
        }

        private void OnGUI()
        {
            try
            {
                KnightEntity k = KnightEntity.Instance;
                if (k == null || !k.IsActive)
                {
                    return;
                }
                // 受击瞬间：屏幕四周黑边立即闪过（与低血量黑边同一纹理），随后渐出
                float hitAlpha = k.HitVignetteAlpha;
                if (hitAlpha > 0.01f)
                {
                    GUI.color = new Color(1f, 1f, 1f, hitAlpha);
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), GetVignetteTexture());
                    GUI.color = Color.white;
                }
                // 凝聚回血成功：屏幕四周短暂亮一下（白色闪光，渐出）
                float focusFlash = k.FocusFlashAlpha;
                if (focusFlash > 0.01f)
                {
                    GUI.color = new Color(1f, 1f, 1f, focusFlash);
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), GetFlashTexture());
                    GUI.color = Color.white;
                }
                // 寻神者护符出现：屏幕四周短时金色闪烁（渐出）
                float ggGold = k.GgGoldFlashAlpha;
                if (ggGold > 0.01f)
                {
                    GUI.color = new Color(1f, 0.84f, 0.2f, ggGold);
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), GetFlashTexture());
                    GUI.color = Color.white;
                }
                // 生命血羁绊回血：屏幕四周短暂蓝色闪光（渐出）
                float lifebloodFlash = k.LifebloodFlashAlpha;
                if (lifebloodFlash > 0.01f)
                {
                    GUI.color = new Color(0.4f, 0.7f, 1f, lifebloodFlash);
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), GetFlashTexture());
                    GUI.color = Color.white;
                }
                // 无忧旋律免伤成功：屏幕四周短暂红色闪光（渐出，同回血白闪渲染逻辑）
                float melodyFlash = k.MelodyFlashAlpha;
                if (melodyFlash > 0.01f)
                {
                    GUI.color = new Color(1f, 0.25f, 0.25f, melodyFlash);
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), GetFlashTexture());
                    GUI.color = Color.white;
                }
                // 仅剩 1 格血：屏幕四周黑色滤镜
                if (k.LowHpVisible)
                {
                    GUI.color = Color.white;
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), GetVignetteTexture());
                }
                // 亡者之怒：屏幕四周红色滤镜（替代低血量黑框）
                if (k.FuryVignetteVisible)
                {
                    GUI.color = Color.white;
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), GetFuryVignetteTexture());
                }
                // 死亡黑屏（淡入/保持/淡出）
                float deathAlpha = k.DeathBlackAlpha;
                if (deathAlpha > 0.01f)
                {
                    GUI.color = new Color(0f, 0f, 0f, deathAlpha);
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
                    GUI.color = Color.white;
                }
                // 虚空解放：出伤瞬间黑屏 0.1s / 后摇瞬间白屏 0.1s
                float voidFlash = k.VoidFlashAlpha;
                if (voidFlash > 0.01f)
                {
                    GUI.color = k.VoidFlashBlack
                        ? new Color(0f, 0f, 0f, voidFlash)
                        : new Color(1f, 1f, 1f, voidFlash);
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
                    GUI.color = Color.white;
                }
                // 虚空解放：屏幕边缘虚空触手（黑屏结束 → 白屏开始，贴底排列循环播放）
                float tentAlpha = k.VoidTentacleAlpha;
                if (tentAlpha > 0.01f)
                {
                    float s = Mathf.Max(0.5f, Screen.height / 1080f);
                    float slot = 5f * 64f * s; // 触手 5 格宽/长（1 格 = 64px @1080p，随分辨率缩放）
                    GUI.color = new Color(1f, 1f, 1f, tentAlpha);
                    DrawVoidTentacleEdge(k, 0, slot, Screen.width);  // 底（原图朝上）
                    DrawVoidTentacleEdge(k, 1, slot, Screen.width);  // 顶（180°）
                    GUI.color = Color.white;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 沿某条屏幕边缘紧密排列虚空触手：edge 0=底 1=顶 2=左 3=右；
        /// slot 为槽位长度，edgeLen 为边缘总长。内容沿边缘铺满槽位，边缘侧贴屏幕边。
        /// </summary>
        private static void DrawVoidTentacleEdge(KnightEntity k, int edge, float slot, float edgeLen)
        {
            try
            {
                int count = Mathf.CeilToInt(edgeLen / Mathf.Max(1f, slot)) + 1;
                for (int i = 0; i < count; i++)
                {
                    var info = k.GetVoidTentacleFrame(k.VoidTentacleOffsetFor(i), edge);
                    if (info.Tex == null)
                    {
                        continue;
                    }
                    float along = Mathf.Max(1f, info.ContentAlong);
                    float scale = slot / along;
                    float drawW = info.W * scale;
                    float drawH = info.H * scale;
                    float gap = Mathf.Clamp01(info.EdgeGapFrac);
                    Rect r;
                    if (edge == 0) // 底：内容底边贴屏幕底
                    {
                        r = new Rect(i * slot, Screen.height - (1f - gap) * drawH, drawW, drawH);
                    }
                    else if (edge == 1) // 顶：内容顶边贴屏幕顶
                    {
                        r = new Rect(i * slot, -gap * drawH, drawW, drawH);
                    }
                    else if (edge == 2) // 左：内容左边贴屏幕左
                    {
                        r = new Rect(-gap * drawW, i * slot, drawW, drawH);
                    }
                    else // 右：内容右边贴屏幕右
                    {
                        r = new Rect(Screen.width - (1f - gap) * drawW, i * slot, drawW, drawH);
                    }
                    GUI.DrawTexture(r, info.Tex, ScaleMode.StretchToFill, true);
                }
            }
            catch (Exception)
            {
            }
        }

        private static Texture2D GetVignetteTexture()
        {
            if (_vignetteTex != null)
            {
                return _vignetteTex;
            }
            int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var cols = new Color[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01((d - 0.55f) / 0.45f);
                    a = a * a;
                    cols[y * size + x] = new Color(0f, 0f, 0f, a * 0.65f);
                }
            }
            tex.SetPixels(cols);
            tex.Apply();
            _vignetteTex = tex;
            return tex;
        }

        /// <summary>
        /// 亡者之怒红色屏幕边框贴图：矩形边框（四角直角），纹理尺寸与屏幕同比例，
        /// 拉伸到全屏后边框厚度均匀、不会变成椭圆形。
        /// </summary>
        private static Texture2D GetFuryVignetteTexture()
        {
            int w = 256;
            int h = Mathf.Max(64, Mathf.RoundToInt(w * Screen.height / (float)Screen.width));
            if (_furyVignetteTex != null && _furyVignetteW == w && _furyVignetteH == h)
            {
                return _furyVignetteTex;
            }
            if (_furyVignetteTex != null)
            {
                UnityEngine.Object.Destroy(_furyVignetteTex);
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var cols = new Color[w * h];
            float t = h * 0.10f; // 边框厚度 = 屏幕短边的 10%，像素均匀
            Color deep = new Color(0.78f, 0.09f, 0.07f); // 比之前更深一点的红色
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float dx = Mathf.Min(x + 0.5f, w - (x + 0.5f));
                    float dy = Mathf.Min(y + 0.5f, h - (y + 0.5f));
                    float d = Mathf.Min(dx, dy); // 到最近屏幕边缘的距离（纹理像素）
                    float a = Mathf.Clamp01((t - d) / t);
                    a = a * a;
                    cols[y * w + x] = new Color(deep.r, deep.g, deep.b, a * 0.8f);
                }
            }
            tex.SetPixels(cols);
            tex.Apply();
            _furyVignetteTex = tex;
            _furyVignetteW = w;
            _furyVignetteH = h;
            return tex;
        }

        /// <summary>凝聚回血成功的白色屏幕闪光贴图（径向渐变：中心透明、四周白色）。</summary>
        private static Texture2D GetFlashTexture()
        {
            if (_flashTex != null)
            {
                return _flashTex;
            }
            int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var cols = new Color[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01((d - 0.5f) / 0.5f);
                    a = a * a;
                    cols[y * size + x] = new Color(1f, 1f, 1f, a * 0.85f);
                }
            }
            tex.SetPixels(cols);
            tex.Apply();
            _flashTex = tex;
            return tex;
        }

        private void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            if (_knightMode)
            {
                ForceNoelGone();
            }
        }

        // ---------- 切换 ----------

        private static void TryToggle()
        {
            if (Time.frameCount == _lastCheckFrame)
            {
                return;
            }
            _lastCheckFrame = Time.frameCount;

            if (!IsTogglePressed())
            {
                return;
            }
            // 护符界面打开期间：T 键在 UI 内用于 格林之子 ↔ 无忧旋律 互换，不切换角色
            if (CharmUiController.Instance != null && CharmUiController.Instance.IsOpen)
            {
                return;
            }
            // 小骑士坐长椅 / 死亡流程 / 虚空解放期间禁用换人键
            if (_knightMode && KnightEntity.Instance != null &&
                (KnightEntity.Instance.IsSitting || KnightEntity.Instance.IsDead ||
                 KnightEntity.Instance.IsVoidLiberating))
            {
                return;
            }
            // 诺艾尔坐在长椅上（原版 BENCH 系列状态，含坐下过渡）时也禁用换人键
            try
            {
                PRNoel prToggle = GetPr();
                if (prToggle != null && prToggle.isBenchOrBenchPreparingState())
                {
                    return;
                }
            }
            catch (Exception)
            {
            }

            _knightMode = !_knightMode;
            KnightInCradlePlugin.KnightModeActive = _knightMode;

            if (_knightMode)
            {
                ActivateKnightMode();
            }
            else
            {
                DeactivateKnightMode();
            }
        }

        private static void ActivateKnightMode()
        {
            PRNoel pr = GetPr();
            if (pr == null)
            {
                _knightMode = false;
                KnightInCradlePlugin.KnightModeActive = false;
                return;
            }

            // 进入骑士模式即快照诺艾尔真实 HP/MP（反射直读字段，不能用 getter——已被劫持）
            SnapshotNoelValues(pr);
            ResetNoelToHealthy(pr);
            // 取消诺艾尔正在蓄力的魔法：清掉预消耗魔力（避免小骑士灵魂条多出蓄力段），
            // 并关闭魔法圈/蓄力特效（蓝色特效不再残留到骑士模式）
            try
            {
                if (pr.Skill != null)
                {
                    pr.Skill.killHoldMagic(false, false, false);
                }
                // 清空特殊魔力条（SpMp）：地雷/黑洞/花环/水碎等“预消耗魔力”魔法
                // 会把自己的消耗段画在魔力条后方并扣减显示上限，切小骑士时一并清掉
                if (pr.SpMp != null)
                {
                    pr.SpMp.Clear();
                }
            }
            catch (Exception)
            {
            }
            HideNoelO2Gauge();
            HideNoel(pr, true);
            HideNoelRenderTicket(pr, true);
            KillShadow(pr);
            RegisterNoelPoseWhitelist(pr);
            // 骑士模式下立绘强制显示“进入战斗”姿态（EMSTATE.BATTLE），
            // 而不是静止在普通站姿；UIPicture.run 在骑士模式下被跳过，姿态保持。
            try
            {
                if (pr.UP != null)
                {
                    pr.UP.changeEmotIn(UIEMOT.STAND, UIPictureBase.EMSTATE.BATTLE);
                }
            }
            catch (Exception)
            {
            }
            // 骑士模式复用诺艾尔原版 HUD：保持 UIStatus 显示（数值由 CombatGuard 覆盖为小骑士数据）
            SetHudVisible(true);
            // 小骑士 HUD 始终显示；切出时重置淡入进度，播放渐显动画
            KnightHudDeco.ResetReveal();
            KnightEntity.SpawnAt(pr);
            MultiplayerCompat.OnKnightModeChanged(); // 切人瞬间重新挂钩（消除联机补丁延迟窗口）
            // 受击判定箱：切换成小骑士后，把诺艾尔碰撞体改为骑士判定箱尺寸，
            // 使怪/玩家打到的是“小骑士的碰撞箱”。脚底仍由 SyncNoelToKnight 按
            // k.FootY - sizey 每帧对齐，避免因尺寸变化导致贴地/出口失步。
            try
            {
                ResizeNoelToKnight(pr, true);
                SyncNoelToKnight(pr);
            }
            catch (Exception)
            {
            }
        }

        private static void DeactivateKnightMode()
        {
            PRNoel pr = GetPr();
            if (pr != null)
            {
                // 方案A：退出骑士模式时还原原生接管状态（走速覆盖、残留 simulate 位）
                NativeBody.ForceDisengage(pr);
                // 切回前先恢复诺艾尔原碰撞体尺寸（后续 setTo 用 pr.sizey 推脚底，需用原值）
                try
                {
                    ResizeNoelToKnight(pr, false);
                }
                catch (Exception)
                {
                }
                UnregisterNoelPoseWhitelist(pr);
                HideNoelRenderTicket(pr, false);
                // 恢复诺艾尔的状态注入（骑士模式期间被禁用）
                try
                {
                    if (pr.SttInjector != null)
                    {
                        pr.SttInjector.enabled = true;
                    }
                }
                catch (Exception)
                {
                }
                // 切回前最后一次恢复快照（骑士模式期间诺艾尔字段可能已被内部逻辑污染）
                RestoreNoelSnapshot(pr);
                // 切回前先把诺艾尔传送到小骑士的位置
                if (KnightEntity.Instance != null)
                {
                    pr.setTo(KnightEntity.Instance.X,
                        KnightEntity.Instance.FootY - pr.sizey);
                }
                // 在狭窄区域（需蹲伏通过）切小骑士后，诺艾尔的蹲伏状态/判定箱可能残留，
                // 切回宽阔区域时表现为脚陷进地里。强制退出蹲伏并重检强制蹲伏。
                try
                {
                    pr.quitCrouch(false, false, false);
                    pr.recheckForceCrouch();
                    System.Reflection.FieldInfo fc = AccessTools.Field(typeof(m2d.M2MoverPr), "t_force_crouch");
                    if (fc != null)
                    {
                        fc.SetValue(pr, 0f);
                    }
                }
                catch (Exception)
                {
                }
                // 贴地处理：恢复物理、清速度、让脚部管理器重新检测地面，
                // 避免在狭窄空间切回时诺艾尔蹲下却悬空
                try
                {
                    M2Phys phy2 = pr.getPhysic();
                    if (phy2 != null)
                    {
                        if (phy2.isPausing())
                        {
                            phy2.Resume();
                        }
                        phy2.killSpeedForce(true, true, true, false, true);
                    }
                    pr.getFootManager()?.initJump(true, false, false);
                }
                catch (Exception)
                {
                }
                HideNoel(pr, false);
                M2PxlAnimatorRT anm = GetAnm(pr);
                if (anm != null)
                {
                    anm.prepareShadowEffect(true);
                }
            }
            // 快照只属于本次骑士模式，切回后清空
            _noelSnapshotHp = _noelSnapshotMaxHp = _noelSnapshotMp = _noelSnapshotMaxMp = -1;
            SetHudVisible(true);
            // 还原骑士模式设置的相机跟随速度，避免切回后镜头仍高速钉在骑士位置/与强制镜头打架
            try
            {
                M2DBase m2d = M2DBase.Instance;
                if (m2d != null && m2d.Cam != null)
                {
                    m2d.Cam.cam_walk_speed_x = m2d.Cam.cam_walk_speed_default;
                    m2d.Cam.cam_walk_speed_y = m2d.Cam.cam_walk_speed_default;
                }
            }
            catch (Exception)
            {
            }
            KnightEntity.Deactivate();
            // 切回诺艾尔：恢复黑暗区域的原版不透明度（小骑士模式调亮的效果只属于小骑士）
            KnightEntity.RestoreDarkAreaBrightness();
            // 切回诺艾尔：恢复雾天雾层的原版不透明度（小骑士模式取消视线遮蔽只属于小骑士）
            KnightEntity.RestoreFogWeatherVisibility();
            // 切回诺艾尔：立绘恢复自动表情更新（重新按诺艾尔状态调整）
            try
            {
                if (pr != null && pr.UP != null)
                {
                    pr.UP.recheck(0, 0);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 进入骑士模式时快照诺艾尔真实 HP/MP 字段。
        /// 必须走反射读字段而非 getter：getter 已被 CombatGuard 劫持为小骑士数值。
        /// hp/mp 未 appear 时初始值可能是 -1000，只有字段有效时才采用快照。
        /// </summary>
        private static void SnapshotNoelValues(PRNoel pr)
        {
            try
            {
                if (pr == null)
                {
                    return;
                }
                int hp = NoelHpField != null ? (int)NoelHpField.GetValue(pr) : -1;
                int maxHp = NoelMaxHpField != null ? (int)NoelMaxHpField.GetValue(pr) : -1;
                int mp = NoelMpField != null ? (int)NoelMpField.GetValue(pr) : -1;
                int maxMp = NoelMaxMpField != null ? (int)NoelMaxMpField.GetValue(pr) : -1;
                if (maxHp > 0 && hp >= 0)
                {
                    _noelSnapshotHp = hp;
                    _noelSnapshotMaxHp = maxHp;
                }
                if (maxMp > 0 && mp >= 0)
                {
                    _noelSnapshotMp = mp;
                    _noelSnapshotMaxMp = maxMp;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>把诺艾尔 HP/MP 字段恢复为快照值（防 getter 劫持被游戏内部逻辑写坏）。</summary>
        private static void RestoreNoelSnapshot(PRNoel pr)
        {
            try
            {
                if (pr == null || _noelSnapshotMaxHp < 0 || _noelSnapshotMaxMp < 0)
                {
                    return;
                }
                if (NoelMaxHpField != null)
                {
                    int cur = (int)NoelMaxHpField.GetValue(pr);
                    if (cur != _noelSnapshotMaxHp)
                    {
                        NoelMaxHpField.SetValue(pr, _noelSnapshotMaxHp);
                    }
                }
                if (NoelHpField != null)
                {
                    int cur = (int)NoelHpField.GetValue(pr);
                    if (cur != _noelSnapshotHp)
                    {
                        NoelHpField.SetValue(pr, Mathf.Clamp(_noelSnapshotHp, 0, _noelSnapshotMaxHp));
                    }
                }
                if (NoelMaxMpField != null)
                {
                    int cur = (int)NoelMaxMpField.GetValue(pr);
                    if (cur != _noelSnapshotMaxMp)
                    {
                        NoelMaxMpField.SetValue(pr, _noelSnapshotMaxMp);
                    }
                }
                if (NoelMpField != null)
                {
                    int cur = (int)NoelMpField.GetValue(pr);
                    if (cur != _noelSnapshotMp)
                    {
                        NoelMpField.SetValue(pr, Mathf.Clamp(_noelSnapshotMp, 0, _noelSnapshotMaxMp));
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 把宿主（诺艾尔）碰撞箱尺寸钉回小骑士尺寸（幂等：尺寸一致时直接返回，不做任何重建）。
        ///
        /// 为什么需要反复执行：诺艾尔的碰撞箱尺寸由游戏自己的 <c>PR.setBounds()</c> 决定 ——
        /// 蹲伏/倒地（DOWN）/受击（DAMAGE_L）/潜行/换姿势等状态切换时它都会重新
        /// <c>Size(12, 68)</c> 像素，把尺寸写回诺艾尔身体大小；而 PrAnimator 在姿势变化时
        /// 会置位 <c>need_check_bounds</c>，下一帧 <c>autoCheckBounds()</c> 就会走到 setBounds。
        /// 所以“切人时改一次尺寸”会被游戏覆盖，表现为：切成小骑士后受击箱/碰撞箱仍是诺艾尔大小。
        ///
        /// 两道保险：
        /// ① <see cref="CombatGuard"/> 的 <c>PR.setBounds</c> 后缀补丁：游戏刚写完立刻拉回；
        /// ② <see cref="SyncNoelToKnight"/> 每帧（Update + LateUpdate）校验一次兜底。
        ///
        /// 关键细节（2026-09-17 修正）：这里只做“**收紧**”，不做“钉死”。
        /// AIC 让玩家钻进矮洞/窄缝靠的就是游戏自己把碰撞箱压小
        /// （蹲伏 CROUCH 40px、压身 PRESSCROUCH 10px；连倒地 DOWN 都会换成 70×20）。
        /// 如果每帧强行写回固定的小骑士尺寸，这些“临时压小”会被立刻撤销，
        /// 表现就是“小骑士过不去原来（诺艾尔）能压身通过的狭窄区域”。
        /// 因此最终尺寸取两者较小值：min(游戏当前值, 小骑士尺寸) ——
        /// 站着时是小骑士尺寸（受击箱不再有诺艾尔那么高），
        /// 而游戏要求更小时依然能继续缩小，通过性与诺艾尔一致。
        /// 宽度同样受此限制，所以小骑士的箱子永远不会比诺艾尔的 12 像素更宽。
        /// </summary>
        internal static void EnforceKnightBodySize(PRNoel pr)
        {
            try
            {
                // 只看“是否处于骑士模式”这个权威标志：切人瞬间（骑士实体刚生成/即将销毁）
                // 也要能立刻套上尺寸，所以不依赖 KnightEntity.Instance 是否存在。
                if (pr == null || !KnightInCradlePlugin.KnightModeActive ||
                    !KnightInCradlePlugin.ResizeHostToKnight)
                {
                    return;
                }
                HostSizeCapMoverUnits(pr.Mp, out float capW, out float capH);
                float tx = Mathf.Min(pr.sizex, capW);
                float ty = Mathf.Min(pr.sizey, capH);
                if (Mathf.Abs(pr.sizex - tx) < 0.0001f && Mathf.Abs(pr.sizey - ty) < 0.0001f)
                {
                    return; // 尺寸已经在限制内：不重建碰撞体
                }
                pr.sizex = tx;
                pr.sizey = ty;
                pr.getColliderCreator()?.fineRecreate();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 宿主碰撞箱上限（mover 单位；1 个单位的数值 = 1 格的半边长，整宽/整高像素 = 数值 × CLENM）。
        /// 由配置 `General/HostColliderWidthPixels / HostColliderHeightPixels` 给出，
        /// 默认 12×30 像素 —— 宽与诺艾尔同宽、高约等于小骑士身高（可调小以钻更矮的缝）。
        /// </summary>
        internal static void HostSizeCapMoverUnits(Map2d mp, out float capW, out float capH)
        {
            float clenm = mp != null ? mp.CLEN * mp.mover_scale : 56f;
            if (clenm <= 0.001f)
            {
                clenm = 56f;
            }
            capW = Mathf.Max(1f, KnightInCradlePlugin.HostWidthPixels) / clenm;
            capH = Mathf.Max(1f, KnightInCradlePlugin.HostHeightPixels) / clenm;
        }

        /// <summary>把诺艾尔碰撞体改为/恢复为小骑士判定箱尺寸，并重建碰撞体。</summary>
        private static void ResizeNoelToKnight(PRNoel pr, bool toKnight)
        {
            try
            {
                if (toKnight)
                {
                    if (!_noelResized)
                    {
                        _noelResized = true;
                        _noelOrigSizex = pr.sizex;
                        _noelOrigSizey = pr.sizey;
                    }
                    EnforceKnightBodySize(pr); // 立即套上骑士尺寸（并记录为“已改过”）
                }
                else
                {
                    // 无论标志位是否同步，都强制恢复原尺寸，防止诺艾尔残留成小骑士的小碰撞箱
                    _noelResized = false;
                    pr.sizex = _noelOrigSizex;
                    pr.sizey = _noelOrigSizey;
                    // 恢复原尺寸时同时恢复物理体（骑士模式期间被暂停以钉死位置）
                    try
                    {
                        M2Phys phy = pr.getPhysic();
                        if (phy != null && phy.isPausing())
                        {
                            phy.Resume();
                        }
                    }
                    catch (Exception)
                    {
                    }
                    // 尺寸恢复后按“当前脚底”重新推导中心，强制重算视觉/碰撞锚点，
                    // 避免诺艾尔脚陷进地里（残留小号 sizey 时脚底锚点会下沉）
                    try
                    {
                        float feet = pr.mbottom;
                        pr.getColliderCreator()?.fineRecreate();
                        pr.setTo(pr.x, feet - pr.sizey);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 把诺艾尔判定箱中心对齐到小骑士判定箱中心：
        /// 未对齐就一直按差值移动，直到对齐；随后清零速度残留并暂停物理体，
        /// 防止 AIC 再把她带离（脚部管理器/重力都会造成晃动）。
        /// Update + LateUpdate 各同步一次，保证渲染与受击判定时重合。
        /// </summary>
        private static void SyncNoelToKnight(PRNoel pr)
        {
            try
            {
                KnightEntity k = KnightEntity.Instance;
                if (k == null || pr == null)
                {
                    return;
                }
                // 宿主碰撞箱尺寸：诺艾尔每次 setBounds（换姿势/蹲伏/倒地/受击）都会把尺寸
                // 写回她自己的身体尺寸，所以这里每帧校验一次（不符才重建碰撞体）。
                EnforceKnightBodySize(pr);
                // 方案A（原生物理模式）：普通贴地行走时诺艾尔物理保持运行，
                // 方向输入已在 PR.runPre 前缀翻译成 simulate_key；这里只做“读回”——
                // 小骑士对齐到诺艾尔（诺艾尔=原生物理真身），不再反向拖拽。
                if (NativeBody.IsNowEligible(pr, k))
                {
                    k.SyncFromHost(pr);
                    pr.need_check_event = true;
                    return;
                }
                float tx = k.X + KnightEntity.HurtCenterX;
                // 修复：纵向改脚底对齐（诺艾尔脚底 == 骑士脚底）。
                // 之前用受击箱中心对齐，不缩放后诺艾尔脚底会高出骑士 0.125 格，
                // 导致镜头/身体高度与骑士不一致（进房后镜头中心上移）。
                // 半高取“实际生效值”（= min(游戏当前值, 小骑士上限)）：即使游戏在别处把 sizey
                // 写回诺艾尔尺寸，宿主的碰撞箱底边也始终贴在小骑士脚底。
                float effSizeY = Mathf.Min(pr.sizey, KnightEntity.HurtSizeY);
                float ty = k.FootY - effSizeY;
                float dx = tx - pr.x;
                float dy = ty - pr.y;
                if (Mathf.Abs(dx) > 0.0001f || Mathf.Abs(dy) > 0.0001f)
                {
                    // 未对齐：按差值移动，直到对齐（不触发脚部重检，避免被拉回地面）
                    pr.moveBy(dx, dy, false);
                }
                // 关键：骑士用 moveBy 直接拖动诺艾尔，不会置位游戏的 need_check_event，
                // 导致 runUi 的事件/出口检测不再运行，小骑士走到房间出口时无法触发转房。
                // 每帧强制置位，让游戏检测到诺艾尔处于出口区域并执行转房事件。
                pr.need_check_event = true;
                M2Phys phy = pr.getPhysic();
                if (phy != null)
                {
                    phy.killSpeedForce(true, true, true, false, true);
                    phy.translate_stack_x = 0f;
                    phy.translate_stack_y = 0f;
                    // 钉死物理体：暂停后 AIC 不再移动诺艾尔（切回诺艾尔时恢复）。
                    // 注意：过图期间不要恢复她的物理，否则会进入下落/无视单向地板状态
                    if (!phy.isPausing())
                    {
                        phy.Pause();
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 蜂群集结：把魔力直接记到诺艾尔账上（破坏魔力草时 +50 MP，见
        /// CharmEffects.CollectorManaWeedMp）。成功返回 true。
        ///
        /// - 骑士模式：诺艾尔 MP 字段每帧被 RestoreNoelSnapshot 拉回快照值，
        ///   因此同时更新快照与真实字段，切回诺艾尔后魔力保留；
        /// - 诺艾尔模式：直接用她的真实 hp/mp 字段（快照机制不参与，避免读到过期快照）。
        /// </summary>
        public static bool GrantNoelMana(float amount)
        {
            try
            {
                if (amount <= 0f)
                {
                    return false;
                }
                PRNoel pr = GetPr();
                if (pr == null)
                {
                    return false;
                }
                int maxMp;
                int cur;
                if (KnightInCradlePlugin.KnightModeActive)
                {
                    // 骑士模式：以快照为准（真实字段每帧会被拉回快照）
                    if (_noelSnapshotMaxMp < 0)
                    {
                        SnapshotNoelValues(pr);
                    }
                    maxMp = _noelSnapshotMaxMp;
                    if (maxMp < 0 && NoelMaxMpField != null)
                    {
                        maxMp = (int)NoelMaxMpField.GetValue(pr);
                    }
                    cur = _noelSnapshotMp >= 0 ? _noelSnapshotMp : 0;
                    if (cur == 0 && NoelMpField != null)
                    {
                        cur = Mathf.Max(0, (int)NoelMpField.GetValue(pr));
                    }
                }
                else
                {
                    // 诺艾尔模式：直接读真实字段，并顺手把快照对齐，避免残留旧值
                    maxMp = NoelMaxMpField != null
                        ? (int)NoelMaxMpField.GetValue(pr)
                        : (int)pr.get_maxmp();
                    cur = NoelMpField != null
                        ? (int)NoelMpField.GetValue(pr)
                        : (int)pr.get_mp();
                    _noelSnapshotMaxMp = maxMp;
                    _noelSnapshotMp = cur;
                }
                if (maxMp <= 0)
                {
                    return false;
                }
                int newMp = Mathf.Min(maxMp, cur + Mathf.CeilToInt(amount));
                _noelSnapshotMp = newMp;
                if (NoelMpField != null)
                {
                    NoelMpField.SetValue(pr, newMp);
                }
                // 满 MP 也照常“吸收”（魔力被消耗、绝不落地给魔物），只是不加数值
                return true;
            }
            catch (Exception)
            {
            }
            return false;
        }

        private static void ForceNoelGone()
        {
            try
            {
                PRNoel pr = GetPr();
                if (pr == null)
                {
                    return;
                }
                HideNoel(pr, true);
                KillShadow(pr);
                // 不再每帧隐藏 HUD：骑士模式复用原版血条/魔力条（数据由 CombatGuard 覆盖）
            }
            catch
            {
            }
        }

        /// <summary>
        /// 小骑士模式下按 ESC 打开游戏菜单。
        /// 复刻 PR.runUi 的菜单判定，但绕过骑士模式下被卡住的 runUi 门控；
        /// 尊重游戏自身的“禁止开菜单”标记（事件/冻结镜头等场景不强行打开），
        /// 菜单已经打开时交给游戏 UI 自身的取消键处理关闭。
        /// </summary>
        private static void TryOpenGameMenuWithEsc()
        {
            try
            {
                if (!UnityEngine.Input.GetKeyDown(KeyCode.Escape))
                {
                    return;
                }
                // 护符界面打开期间 ESC 完全无效（既不关界面也不开设置）
                if (CharmUiController.Instance != null && CharmUiController.Instance.IsOpen)
                {
                    return;
                }
                NelM2DBase nm2d = M2DBase.Instance as NelM2DBase;
                if (nm2d == null || nm2d.curMap == null || !nm2d.pre_map_active)
                {
                    return;
                }
                // 指南针：战斗中也能打开地图传送（绕过菜单禁用标记）。
                // 按"当前操控角色"判断：骑士模式看小骑士那套，诺艾尔模式看诺艾尔那套。
                bool compassOpen = CharmEffects.IsEquippedForCurrentPlayer(CharmEffects.CompassId);
                if (!compassOpen && !nm2d.can_open_gamemenu)
                {
                    return;
                }
                if (nm2d.GM != null && nm2d.GM.isActive())
                {
                    return;
                }
                nm2d.menu_open = NelM2DBase.MENU_OPEN.OPEN;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 小骑士模式下 M 键打开地图：复刻 PR.runUi 的 M 键判定，但绕过骑士模式下
        /// 被卡住的 runUi 输入门控，直接走 AIC 原生的 menu_open = OPEN_MAP 通道。
        /// 坐在长椅上时打开的地图会启用快速旅行（可选中其他长椅传送）。
        /// </summary>
        private static void TryOpenMapWithM()
        {
            try
            {
                if (!UnityEngine.Input.GetKeyDown(KeyCode.M))
                {
                    return;
                }
                // 护符界面打开期间 M 也不生效
                if (CharmUiController.Instance != null && CharmUiController.Instance.IsOpen)
                {
                    return;
                }
                NelM2DBase nm2d = M2DBase.Instance as NelM2DBase;
                if (nm2d == null || nm2d.curMap == null || !nm2d.pre_map_active || !nm2d.can_open_gamemenu)
                {
                    return;
                }
                if (nm2d.GM != null && nm2d.GM.isActive())
                {
                    return;
                }
                PRNoel pr = GetPr();
                if (pr == null || !pr.is_alive || pr.isDamagingOrKo())
                {
                    return;
                }
                nm2d.menu_open = NelM2DBase.MENU_OPEN.OPEN_MAP;
            }
            catch (Exception)
            {
            }
        }

        // ---------- 工具 ----------

        /// <summary>
        /// 过图期间恢复诺艾尔物理：骑士模式每帧同步会暂停她，若不恢复，
        /// AIC 的进门强制位移推不动诺艾尔，小骑士也会被带回原房间。
        /// 恢复后小骑士在重定位阶段跟随诺艾尔完成进门。
        /// </summary>
        public static void ResumeNoelForTransfer()
        {
            try
            {
                PRNoel pr = GetPr();
                if (pr == null)
                {
                    return;
                }
                M2Phys phy = pr.getPhysic();
                if (phy != null)
                {
                    // 注意：这里不再强行恢复物理——由 KnightEntity 的
                    // _pendingRepositionActive 标志告知 Behaviour 何时恢复/暂停
                    phy.killSpeedForce(true, true, true, false, true);
                }
                // 传送脚本可能模拟过蹲伏键（B），结束后残留蹲伏会让出门触发异常；强制退出
                QuitNoelCrouch(pr);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>重定位结束/任意时刻强制把小骑士同步到诺艾尔（对齐 + 暂停诺艾尔物理）。</summary>
        public static void SyncNoelToKnightPublic()
        {
            try
            {
                PRNoel pr = GetPr();
                if (pr != null)
                {
                    SyncNoelToKnight(pr);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>强制诺艾尔退出蹲伏状态（传送/过图残留时调用）。</summary>
        private static void QuitNoelCrouch(PRNoel pr)
        {
            try
            {
                pr.quitCrouch(false, false, false);
                pr.recheckForceCrouch();
                System.Reflection.FieldInfo fc = AccessTools.Field(typeof(m2d.M2MoverPr), "t_force_crouch");
                if (fc != null)
                {
                    fc.SetValue(pr, 0f);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 过图后刷新：AIC 传送脚本会隐藏/重建诺艾尔碰撞体，可能把尺寸改回默认，
        /// 导致后续过图触发异常。这里重新把小骑士碰撞体套到诺艾尔，并重新对齐、暂停。
        /// </summary>
        public static void RefreshNoelForKnight()
        {
            try
            {
                PRNoel pr = GetPr();
                if (pr == null || KnightEntity.Instance == null)
                {
                    return;
                }
                // 修复 0：不缩放诺艾尔碰撞体（保持原版 FootD 正常）
                M2Phys phy = pr.getPhysic();
                // 物理恢复状态下硬传送，保证 Rigidbody/碰撞体/相机都同步到骑士位置
                if (phy != null && phy.isPausing())
                {
                    phy.Resume();
                }
                // 硬性把诺艾尔放到骑士脚底（模拟切人时的 setTo），
                // 确保出门传送点能检测到她（传送后她可能滞留在入口）
                pr.setTo(KnightEntity.Instance.X,
                    KnightEntity.Instance.FootY - pr.sizey);
                pr.getColliderCreator()?.fineRecreate();
                // 清除传送残留的蹲伏状态（关键：蹲伏的诺艾尔无法触发出门传送点）
                QuitNoelCrouch(pr);
                SyncNoelToKnight(pr);
                // 镜头立即对焦骑士：AIC 传送点只在相机附近激活，
                // 镜头滞留会导致出门传送点不触发（“镜头没跟过去”）
                try
                {
                    M2DBase.Instance?.Cam.fineImmediately();
                }
                catch (Exception)
                {
                }
            }
            catch (Exception)
            {
            }
        }

        private static PRNoel GetPr()
        {
            NelM2DBase m2d = M2DBase.Instance as NelM2DBase;
            return m2d != null ? m2d.getPrNoel() : null;
        }

        private static M2PxlAnimatorRT GetAnm(PRNoel pr)
        {
            PrNoelAnimator anmN = pr.getAnimatorN();
            if (anmN == null)
            {
                return null;
            }
            return AnmField.GetValue(anmN) as M2PxlAnimatorRT;
        }

        private static void HideNoel(PRNoel pr, bool hide)
        {
            M2PxlAnimatorRT anm = GetAnm(pr);
            if (anm != null)
            {
                anm.alpha = hide ? 0f : 1f;
            }
        }

        /// <summary>
        /// 彻底隐藏/恢复诺艾尔身体：停用/重建渲染票据。
        /// alpha=0 可能被雾/状态效果用缓存的不透明网格绕过，停用票据后身体根本不绘制。
        /// </summary>
        private static void HideNoelRenderTicket(PRNoel pr, bool hide)
        {
            try
            {
                M2PxlAnimatorRT anm = GetAnm(pr);
                if (anm == null || NoelRtkField == null)
                {
                    return;
                }
                if (hide)
                {
                    M2RenderTicket t = NoelRtkField.GetValue(anm) as M2RenderTicket;
                    if (t != null)
                    {
                        try
                        {
                            _noelRtkOrder = t.order;
                        }
                        catch (Exception)
                        {
                        }
                        M2DBase md = M2DBase.Instance;
                        if (md != null && md.Cam != null && md.Cam.MovRender != null)
                        {
                            NoelRtkField.SetValue(anm, md.Cam.MovRender.deassignDrawable(t, -1));
                        }
                    }
                }
                else
                {
                    M2RenderTicket t = NoelRtkField.GetValue(anm) as M2RenderTicket;
                    if (t == null && anm.getMainMeshDrawer() != null)
                    {
                        anm.initRenderTicket(_noelRtkOrder);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 诺艾尔动画白名单：小骑士模式下只允许 站立(stand)/走路(walk)/跳跃(jump) 三个姿势，
        /// 其它姿势（雾气呛咳/受击/中毒/表情等）一律按小骑士状态改写，
        /// 保证后台诺艾尔不会因负面效果播放其它动画。
        /// </summary>
        private static string NoelPoseWhitelistListener(M2PxlAnimator This, string pose0, ref int restart_anim)
        {
            if (!_knightMode || KnightEntity.Instance == null)
            {
                return pose0;
            }
            // 【2026-09-17】默认完全不动宿主的姿势（HostPoseOverride=false）：
            // 宿主是隐藏渲染的，姿势只影响游戏自己的状态机 —— 蹲伏/爬行/趴下/压扁/爬梯/游泳
            // 全都依赖她自己的姿势推进。之前用“小骑士状态”覆盖成 stand/walk/jump，
            // 会把游戏自己的“趴下钻过矮缝”等动作取消掉（forest_ahletic_home_thorn 的教程就是教这个）。
            if (!KnightInCradlePlugin.HostPoseOverride)
            {
                return pose0;
            }
            // 【关键修复 2026-09-17】游戏要求蹲伏/爬行/倒地时必须放行原姿势。
            //
            // AIC 让玩家钻过矮洞、窄缝，靠的就是 CROUCH（12×40 像素）/ PRESSCROUCH（12×10 像素）
            // 姿势 + 与之配套的小碰撞箱；而 PR.setBounds 判断“要不要蹲”时会看 Anm 的姿势
            // （poseIs(POSE_TYPE.CROUCH/DOWN)）。之前这里无条件把姿势顶回 stand/walk/jump，
            // 等于把游戏的蹲伏/爬行整个取消 —— 姿势变回站立后 setBounds 又把体型放回
            // 诺艾尔站立尺寸（12×68 像素），表现就是“小骑士过不去某些地形”，
            // 而且日志里碰撞体始终是 12.0×68.0（诺艾尔站立）。
            //
            // 宿主是被隐藏渲染的（见 HideNoelRenderTicket / alpha=0），姿势外观无影响，
            // 所以这些状态下直接放行，让游戏自己完成蹲伏/爬行/倒地流程。
            // 两种判据都要：① 状态已经是蹲伏/趴下（is_crouch / bounds_）；
            // ② 游戏这次**请求的姿势本身就是**蹲伏/爬行/趴下/倒地 —— 因为游戏可能是
            // “先摆姿势、后改状态”，只看状态会在顺序上漏掉，导致姿势被覆盖、
            // 随后 setBounds 又把体型写回诺艾尔站立尺寸。
            // 另：玩家按住“下”键时（= 主动要蹲/趴，教程就是教这个）一律放行，
            // 让游戏自己推进 crouch/prone/press 全流程。
            bool downHeld = KeyConfig.GetHeld(KnightInCradlePlugin.LookDownKey, KeyCode.Mouse1);
            if (IsHostCrouchOrDownPose() || IsSqueezePoseName(pose0) || downHeld)
            {
                return pose0;
            }
            KnightEntity k = KnightEntity.Instance;
            if (k.Grounded)
            {
                // 小骑士在地面：在走路 → walk，静止 → stand
                return Mathf.Abs(k.Vx) > 0.05f ? "walk" : "stand";
            }
            // 小骑士不在地面：跳跃
            return "jump";
        }

        /// <summary>
        /// 宿主当前是否处于“游戏要求的蹲伏 / 爬行 / 倒地”状态：
        /// <c>pr.is_crouch</c>（= crouching &gt; 0 或 t_force_crouch &gt; 0）为真，
        /// 或 <c>bounds_</c> 不是 NORMAL（CROUCH / CROUCH_WIDE / DOWN / DAMAGE_L / PRESSCROUCH / EVADE_JUMP）。
        /// 这些状态下必须放行游戏自己的姿势，否则会取消“钻过矮洞/窄缝”的能力。
        /// </summary>
        private static bool IsHostCrouchOrDownPose()
        {
            try
            {
                PRNoel pr = GetPr();
                if (pr == null)
                {
                    return false;
                }
                if (pr.is_crouch)
                {
                    return true;
                }
                FieldInfo fb = AccessTools.Field(typeof(m2d.M2MoverPr), "bounds_");
                if (fb != null)
                {
                    object b = fb.GetValue(pr);
                    return b != null && b.ToString() != "NORMAL";
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>
        /// 游戏请求的姿势名是否属于“钻行/趴下”类（放行给游戏本体，别覆盖）：
        /// crouch / crouch_wide / press_crouch / crawl / down / sink / ladder / rope 等。
        /// 名字用包含匹配（不同版本/不同角色的姿势命名可能带前后缀）。
        /// </summary>
        private static bool IsSqueezePoseName(string pose0)
        {
            if (string.IsNullOrEmpty(pose0))
            {
                return false;
            }
            string p = pose0.ToLowerInvariant();
            return p.Contains("crouch") || p.Contains("crawl") || p.Contains("press") ||
                   p.Contains("down") || p.Contains("sink") || p.Contains("ladder") ||
                   p.Contains("rope") || p.Contains("slide") || p.Contains("squat") ||
                   p.Contains("lie") || p.Contains("swim") || p.Contains("water") ||
                   p.Contains("damage");
        }

        private static void RegisterNoelPoseWhitelist(PRNoel pr)
        {
            try
            {
                M2PxlAnimatorRT anm = GetAnm(pr);
                if (anm != null)
                {
                    anm.fnChangePoseListener = NoelPoseWhitelistListener;
                }
            }
            catch (Exception)
            {
            }
        }

        private static void UnregisterNoelPoseWhitelist(PRNoel pr)
        {
            try
            {
                M2PxlAnimatorRT anm = GetAnm(pr);
                if (anm != null && anm.fnChangePoseListener == NoelPoseWhitelistListener)
                {
                    anm.fnChangePoseListener = null;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 进入骑士模式时把诺艾尔重置为“健康/无状态”基准态：
        /// 清空全部状态效果（ser_bits→0）、复位 PR.STATE.NORMAL、清风压计时。
        /// 负面效果已由 CombatGuard 的状态级白名单拦截（changeState 只允许 NORMAL、
        /// M2Ser.Add 全拦截、毒雾/风压拦截），这里只处理切人前已存在/绕过拦截的残留。
        /// </summary>
        private static void ResetNoelToHealthy(PRNoel pr)
        {
            try
            {
                if (pr.Ser != null)
                {
                    pr.Ser.clear();
                }
            }
            catch (Exception)
            {
            }
            // 禁用状态注入：changeState(NORMAL) 会被 SttInjector（呛咳/陷阱/冻结等）覆盖回负面状态，
            // 骑士模式下诺艾尔不应处于任何注入状态，直接关掉注入器，切回诺艾尔时再恢复
            try
            {
                if (pr.SttInjector != null)
                {
                    pr.SttInjector.enabled = false;
                }
            }
            catch (Exception)
            {
            }
            try
            {
                pr.changeState(PR.STATE.NORMAL);
            }
            catch (Exception)
            {
            }
            try
            {
                FieldInfo fw = AccessTools.Field(typeof(PR), "wind_apply_t");
                if (fw != null)
                {
                    fw.SetValue(pr, 0f);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>隐藏 O2 呼吸条（诺艾尔切骑士前可能在毒雾/水呛中，条已显示）。</summary>
        private static void HideNoelO2Gauge()
        {
            try
            {
                if (UIStatus.Instance == null)
                {
                    return;
                }
                FieldInfo fg = AccessTools.Field(typeof(UIStatus), "O2Gauge");
                if (fg == null)
                {
                    return;
                }
                object gage = fg.GetValue(UIStatus.Instance);
                if (gage == null)
                {
                    return;
                }
                PropertyInfo pv = AccessTools.Property(gage.GetType(), "value");
                if (pv != null)
                {
                    pv.SetValue(gage, 100f);
                }
                FieldInfo fgob = AccessTools.Field(gage.GetType(), "Gob");
                if (fgob != null && fgob.GetValue(gage) is GameObject gob)
                {
                    gob.SetActive(false);
                }
            }
            catch (Exception)
            {
            }
        }

        private static void KillShadow(PRNoel pr)
        {
            M2PxlAnimatorRT anm = GetAnm(pr);
            if (anm == null)
            {
                return;
            }
            object ef = ShadowField.GetValue(anm);
            if (ef is EffectItem item)
            {
                item.destruct();
                ShadowField.SetValue(anm, null);
            }
        }

        private static void SetHudVisible(bool visible)
        {
            try
            {
                if (UIStatus.Instance != null)
                {
                    GameObject gob = UIStatus.Instance.GetGob();
                    if (gob != null)
                    {
                        gob.SetActive(visible);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        // ---------- 按键 ----------

        /// <summary>公开的诺艾尔获取器（供 KnightEntity 清理残留状态用）。</summary>
        public static PRNoel GetPrPublic()
        {
            return GetPr();
        }

        private static bool IsTogglePressed()
        {
            string keyName = KnightInCradlePlugin.ToggleKey.Value;

            if (!Enum.TryParse(keyName, out KeyCode legacyKey))
            {
                legacyKey = KeyCode.T;
            }

            try
            {
                if (Keyboard.current != null && Enum.TryParse(keyName, out Key inputSystemKey))
                {
                    if (Keyboard.current[inputSystemKey].wasPressedThisFrame)
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
            }

            try
            {
                return UnityEngine.Input.GetKeyDown(legacyKey);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------- Harmony ----------

        private static void ApplyHarmony()
        {
            try
            {
                var harmony = new Harmony("dev.KnightInCradle");
                Type sceneGame = AccessTools.TypeByName("nel.SceneGame");
                if (sceneGame == null)
                {
                    return;
                }

                // SceneGame.Update 的补丁只是补一次 TryToggle 调用（本类 Update 每帧也会调），
                // 在部分游戏版本（0.29j）上该方法的 IL 会让 Harmony 编译失败。
                // 这里单独隔离：失败不阻断 CombatGuard 的核心补丁（HUD/伤害/输入守卫）。
                try
                {
                    MethodInfo update = AccessTools.Method(sceneGame, "Update");
                    if (update != null)
                    {
                        harmony.Patch(update, postfix: new HarmonyMethod(
                            typeof(KnightInCradleBehaviour).GetMethod(nameof(SceneGameUpdatePostfix),
                                BindingFlags.Static | BindingFlags.Public)));
                    }
                }
                catch (Exception)
                {
                }
                _harmonyApplied = true;
                CombatGuard.Apply(harmony);
                CharmUiInputPatch.Apply(harmony);
                CharmEffects.Apply(harmony);
                NativeBody.Apply(harmony);
                // 启动体检：DLL 版本 + 补丁挂载结果（失败项带原因）。
                // 排查“改动没生效”类问题时先看这一行。
                // 另外打印“本次实际加载的是哪个文件 + 文件时间”：plugins 目录里若存在两份
                // KnightInCradle.dll（例如根目录留了一份旧的），BepInEx 只会加载其中一份，
                // 靠这一行就能立刻确认到底跑的是哪份，避免“改了没生效”。
                string selfDll = "?";
                string selfDllTime = "?";
                try
                {
                    selfDll = Assembly.GetExecutingAssembly().Location;
                    selfDllTime = File.GetLastWriteTime(selfDll).ToString("yyyy-MM-dd HH:mm:ss");
                }
                catch (Exception)
                {
                }
                KnightInCradlePlugin.PluginLog?.LogInfo(
                    "[KIC][补丁] KnightInCradle build=" + SelfBuildTag + " 提交=" + SourceRevisionStamp() + " " +
                    CombatGuard.GetPatchResult() +
                    " dll=" + selfDll + " (" + selfDllTime + ")" +
                    " 编译目标=" + CompileTargetStamp() + " 运行时游戏程序集=" + RuntimeGameAssemblyStamp());
                KnightInCradlePlugin.PluginLog?.LogInfo(
                    "[KIC][配置] NativeBodyMode=" + (NativeBody.ConfigValue ? "true" : "false") +
                    "（false=行走/跳跃走模组自己的手写物理（默认，联机版同款）；" +
                    "true=诺艾尔原生物理接管地面行走，" +
                    "已知会出现「跳跃失灵、平台边缘浮空、要在斜面上走一会才恢复」）" +
                    " ResizeHostToKnight=" + (KnightInCradlePlugin.ResizeHostToKnight ? "true" : "false") +
                    " ForceHostCrouch=" + (KnightInCradlePlugin.ForceHostCrouch ? "true" : "false") +
                    " HostPoseOverride=" + (KnightInCradlePlugin.HostPoseOverride ? "true" : "false") +
                    " HostCollider=" + KnightInCradlePlugin.HostWidthPixels + "x" +
                    KnightInCradlePlugin.HostHeightPixels + "px");
                // 原生物理接管（NativeBodyMode=true）是已知会让“跳跃失灵 / 平台边缘浮空”的实验开关，
                // 单独给一行警告，避免再被误开启后当成模组 bug 排查。
                if (NativeBody.ConfigValue)
                {
                    KnightInCradlePlugin.PluginLog?.LogWarning(
                        "[KIC][警告] NativeBodyMode=true：地面行走由诺艾尔原生物理接管，" +
                        "会出现跳跃失灵、走到平台边缘浮空（斜面上走一会才恢复）。" +
                        "把 BepInEx/config/dev.KnightInCradle.cfg 里的 NativeBodyMode 改回 false 即可恢复。");
                }
            }
            catch (Exception)
            {
            }
        }

        public static void SceneGameUpdatePostfix()
        {
            TryToggle();
            // 切回诺艾尔后仍在飞的吸虫：骑士实体已停用，这里继续驱动它们
            // （否则 Update() 提前 return，吸虫会僵在半空）
            KnightEntity.TickOrphanFlukes();
        }

        /// <summary>
        /// 本 DLL 编译时用的“目标游戏程序集”标记（csproj 写入的 AssemblyMetadata）。
        /// 与下面 RuntimeGameAssemblyStamp() 对照，就能立刻看出手上的 DLL 是否配错版本
        /// ——配错版本的典型症状是 `[KIC][补丁] … N 失败（IL Compile Error）`。
        /// </summary>
        private static string CompileTargetStamp()
        {
            try
            {
                object[] attrs = Assembly.GetExecutingAssembly()
                    .GetCustomAttributes(typeof(AssemblyMetadataAttribute), false);
                foreach (object o in attrs)
                {
                    if (o is AssemblyMetadataAttribute m && m.Key == "AicCompileTarget")
                    {
                        return m.Value;
                    }
                }
            }
            catch (Exception)
            {
            }
            return "(未记录)";
        }

        /// <summary>运行时实际加载的游戏程序集（Assembly-CSharp）文件大小/时间。</summary>
        private static string RuntimeGameAssemblyStamp()
        {
            try
            {
                string path = typeof(nel.NelEnemy).Assembly.Location;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return "(路径不可用)";
                }
                var fi = new FileInfo(path);
                return fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + " / " + fi.Length + " bytes";
            }
            catch (Exception)
            {
            }
            return "(读取失败)";
        }

        /// <summary>
        /// 本次 DLL 内嵌的源码提交号（csproj 未关闭 SourceRevisionId 时，
        /// AssemblyInformationalVersion 会写成 "0.2.0+&lt;git 提交&gt;"）。
        /// 和 build= 标记、dll 路径/文件时间放在同一行，用来回答“这局跑的到底是哪次提交”。
        /// 工程还没有 git 仓库、或用了 -p:IncludeSourceRevisionInInformationalVersion=false 时显示 (无)。
        /// </summary>
        private static string SourceRevisionStamp()
        {
            try
            {
                var attr = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    Assembly.GetExecutingAssembly(), typeof(AssemblyInformationalVersionAttribute));
                string v = attr != null ? attr.InformationalVersion : null;
                if (string.IsNullOrEmpty(v))
                {
                    return "(无)";
                }
                int i = v.IndexOf('+');
                if (i < 0 || i + 1 >= v.Length)
                {
                    return "(无)";
                }
                string rev = v.Substring(i + 1);
                return rev.Length > 7 ? rev.Substring(0, 7) : rev;
            }
            catch (Exception)
            {
            }
            return "(读取失败)";
        }
    }
}
