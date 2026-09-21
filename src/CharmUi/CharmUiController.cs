using System;
using System.Collections.Generic;
using nel;
using UnityEngine;

namespace KnightInCradle.CharmUi
{
    /// <summary>
    /// 护符 UI 行为控制器：
    /// - 打开/关闭（U 键门控在 Behaviour 侧：小骑士模式 + 游戏内）；
    /// - 光标导航：第 0 行为上方已装备栏（含固定虚空之心与空槽），下方为护符网格；
    /// - 只有坐长椅时才能装配/卸下；否则仅可查看信息；
    /// - 槽位规则：11 个 cost_black 容量，装配按总花费从左到右换成 cost_white，
    ///   超额时右侧显示 cost_overcharm 且无法继续装配。
    /// </summary>
    public sealed class CharmUiController
    {
        public static CharmUiController Instance;

        private readonly CharmUiOnGuiLayer _layer;
        private readonly List<int> _equippedIds = new List<int>();
        private readonly List<int> _gridIds = new List<int>();

        private int _cursorRow;      // 0 = 上方已装备栏，1.. 网格行
        private int _cursorCol;
        private int _columns = 10;
        private bool _open;
        private bool _closing;
        private bool _sitting;
        private float _progress = 1f;   // 0=收起 1=完全展开
        private const float OpenDuration = 0.25f; // 打开：从中间向两边逐渐显示（0.25s）
        // 自限 sign：本存档内点击计数（前 3 次 chain_cut，第 4 次解锁 gg_godseeker_mode_selector）
        private const string GgClicksKey = "kic_gg_clicks";
        // 格林之子 ↔ 无忧旋律 变体状态：本存档内格林之子槽位当前显示哪个变体（存 43=无忧旋律，0=格林之子）
        private const string VariantKey = "kic_charm_variant";
        private static readonly string[] GgButtonKeys =
            { CharmEffects.GgNailKey, CharmEffects.GgMaskKey, CharmEffects.GgCharmKey, CharmEffects.GgSoulKey };
        // 装卸平移动画：护符在 0.2s 内从选中位置 ↔ 已装备区平移；音效延迟到动画到达时播放
        private int _flyId;             // 正在平移的护符 id（0=无动画）
        private bool _flyEquip;         // true=装备（网格→已装备区） false=卸下（已装备区→网格）
        private int _flyIndex;          // 已装备栏槽位索引（装备=目标槽位，卸下=原槽位）
        private float _flyTimer;
        private bool _flyOvercharm;     // 到达时播放超载音效
        private bool _flySave;          // 到达时额外播放 ui_save
        private const float FlyDuration = 0.1f;
        private float _signShake;       // 剩余 UI 震动幅度（像素，随时间衰减）

        public CharmUiController(CharmUiOnGuiLayer layer)
        {
            Instance = this;
            _layer = layer;
            _equippedIds.Add(CharmDatabase.FixedCharmId); // 虚空之心固定装备
            // 读档发生在控制器创建前时，按已装备快照恢复
            if (CharmSave.EquippedSnapshot.Count > 0)
            {
                ApplyEquippedFromSave(new List<int>(CharmSave.EquippedSnapshot));
            }
            _columns = Mathf.Clamp(
                KnightInCradlePlugin.CharmUiColumns != null ? KnightInCradlePlugin.CharmUiColumns.Value : 10,
                1, 20);
            BuildGridIds();
            MoveCursorTo(0, 0);
        }

        public bool IsOpen => _open || _closing;
        public bool IsSitting => _sitting;
        public List<int> EquippedIds => _equippedIds;
        public int SelectedId { get; private set; } = -1;
        public int TotalCost { get; private set; }
        public bool Overcharmed => TotalCost > CharmDatabase.NotchCapacity;
        /// <summary>水平展开系数（0=收起 1=展开），供渲染层做开合动画。</summary>
        public float OpenScale => _progress;
        /// <summary>是否有护符正在平移。</summary>
        public bool IsFlying => _flyId != 0;
        public int FlyId => _flyId;
        public bool FlyEquipping => _flyEquip;
        public int FlyEquipIndex => _flyIndex;
        /// <summary>平移进度 0~1（0=起点 1=终点）。</summary>
        public float FlyProgress => _flyId != 0 ? Mathf.Clamp01(1f - _flyTimer / FlyDuration) : 1f;
        /// <summary>sign 点击的 UI 震动幅度（像素；0=不震）。</summary>
        public float SignShake => _signShake;
        /// <summary>寻神者模式选择器是否已解锁显示（sign 第 4 次点击后）。</summary>
        public bool IsGgSelectorShown => COOK.getSF(GgClicksKey) >= 4;
        /// <summary>GG 按钮状态（0=骨钉 1=外壳 2=护符 3=灵魂），返回 1 或 2。</summary>
        public int GetGgButtonState(int idx)
        {
            if (idx < 0 || idx >= GgButtonKeys.Length)
            {
                return 1;
            }
            return COOK.getSF(GgButtonKeys[idx]) == 2 ? 2 : 1;
        }
        public int CursorRow => _cursorRow;
        public int CursorCol => _cursorCol;

        /// <summary>光标所在网格的护符 id（上方栏时为 -1）。</summary>
        public int CursorGridId
        {
            get
            {
                if (_cursorRow <= 0)
                {
                    return -1;
                }
                int idx = (_cursorRow - 1) * _columns + _cursorCol;
                return idx >= 0 && idx < _gridIds.Count ? _gridIds[idx] : -1;
            }
        }

        /// <summary>图片文件是否为已装备（含固定虚空之心）护符的图标——用于隐藏下层网格图标。</summary>
        public bool IsIconHidden(string file)
        {
            if (string.IsNullOrEmpty(file))
            {
                return false;
            }
            foreach (int id in _equippedIds)
            {
                CharmData cd = CharmDatabase.Get(id);
                if (cd != null &&
                    file.StartsWith(cd.IconFile, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            // 格林之子 ↔ 无忧旋律 互斥显示：当前变体不是哪个，就隐藏哪个的网格图标
            CharmData grimm = CharmDatabase.Get(CharmEffects.GrimmId);
            if (grimm != null &&
                file.StartsWith(grimm.IconFile, StringComparison.OrdinalIgnoreCase) &&
                !_gridIds.Contains(CharmEffects.GrimmId))
            {
                return true;
            }
            CharmData melody = CharmDatabase.Get(CharmEffects.MelodyId);
            if (melody != null &&
                file.StartsWith(melody.IconFile, StringComparison.OrdinalIgnoreCase) &&
                !_gridIds.Contains(CharmEffects.MelodyId))
            {
                return true;
            }
            return false;
        }

        /// <summary>是否坐着（可装配）——由 Behaviour 每帧刷新。</summary>
        public void RefreshSitting(bool sitting)
        {
            _sitting = sitting;
        }

        /// <summary>读档后恢复装备列表（固定虚空之心恒在首位，之后按存档顺序）。</summary>
        public void ApplyEquippedFromSave(List<int> saved)
        {
            _equippedIds.Clear();
            _equippedIds.Add(CharmDatabase.FixedCharmId);
            if (saved != null)
            {
                foreach (int id in saved)
                {
                    if (id == CharmDatabase.FixedCharmId || _equippedIds.Contains(id) ||
                        _equippedIds.Count >= CharmDatabase.NotchCapacity + 1)
                    {
                        continue;
                    }
                    if (CharmDatabase.Get(id) != null)
                    {
                        _equippedIds.Add(id);
                    }
                }
            }
            ApplySavedVariant(); // 读档后按新存档的变体状态还原格林之子/无忧旋律槽位
            RecalcCost();
            RefreshSelection();
        }

        public void Open()
        {
            _open = true;
            _closing = false;
            _progress = 0f;
            _layer.SetVisible(true);
            MoveCursorTo(0, 0);
            RefreshSelection();
        }

        public void Close()
        {
            if (!_open && !_closing)
            {
                return;
            }
            _open = false;
            _closing = true; // 播放收回动画，结束后隐藏
        }

        /// <summary>关闭并隐藏渲染层（供 ESC/M 等外部路径调用）。</summary>
        public void CloseAndHide()
        {
            _open = false;
            _closing = false;
            _progress = 0f;
            _layer.SetVisible(false);
        }

        public void Toggle()
        {
            if (_open)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        /// <summary>每帧驱动：输入 + 状态刷新。</summary>
        public void Update()
        {
            // sign 点击的 UI 震动衰减
            if (_signShake > 0f)
            {
                _signShake = Mathf.Max(0f, _signShake - Time.unscaledDeltaTime * 45f);
            }
            // 装卸平移动画推进：到达终点时播放延迟音效
            if (_flyId != 0)
            {
                _flyTimer -= Time.unscaledDeltaTime;
                if (_flyTimer <= 0f)
                {
                    _flyTimer = 0f;
                    int doneId = _flyId;
                    bool doneEquip = _flyEquip;
                    bool doneOver = _flyOvercharm;
                    bool doneSave = _flySave;
                    _flyId = 0;
                    if (doneEquip)
                    {
                        if (doneOver)
                        {
                            CharmAudio.Overcharm();
                        }
                        else
                        {
                            CharmAudio.Equip();
                            if (doneSave)
                            {
                                CharmAudio.Save(); // 成功装配额外播放 ui_save
                            }
                        }
                    }
                    else
                    {
                        CharmAudio.Equip();
                    }
                }
            }
            // 开合动画推进（打开 0.5 秒 / 关闭 0.85 秒）
            if (_open && _progress < 1f)
            {
                _progress = Mathf.Min(1f, _progress + Time.unscaledDeltaTime / OpenDuration);
            }
            else if (_closing)
            {
                // 关闭不做动画：按 O 立即隐藏
                _progress = 0f;
                _closing = false;
                _layer.SetVisible(false);
            }
            if (!_open)
            {
                return;
            }

            int dx = 0;
            int dy = 0;
            // 游戏方向键（上=Q、下=鼠标右键、左=A、右=D，读配置），键盘方向键作为备选
            KeyCode keyUp = KeyConfig.Parse(
                KnightInCradlePlugin.LookUpKey != null ? KnightInCradlePlugin.LookUpKey.Value : null, KeyCode.Q);
            KeyCode keyDown = KeyConfig.Parse(
                KnightInCradlePlugin.LookDownKey != null ? KnightInCradlePlugin.LookDownKey.Value : null, KeyCode.Mouse1);
            KeyCode keyLeft = KeyConfig.Parse(
                KnightInCradlePlugin.MoveLeftKey != null ? KnightInCradlePlugin.MoveLeftKey.Value : null, KeyCode.A);
            KeyCode keyRight = KeyConfig.Parse(
                KnightInCradlePlugin.MoveRightKey != null ? KnightInCradlePlugin.MoveRightKey.Value : null, KeyCode.D);
            if (RawDown(keyLeft) || KeyDown(KeyCode.LeftArrow, UnityEngine.InputSystem.Key.LeftArrow)) dx = -1;
            else if (RawDown(keyRight) || KeyDown(KeyCode.RightArrow, UnityEngine.InputSystem.Key.RightArrow)) dx = 1;
            if (RawDown(keyUp) || KeyDown(KeyCode.UpArrow, UnityEngine.InputSystem.Key.UpArrow)) dy = -1;
            else if (RawDown(keyDown) || KeyDown(KeyCode.DownArrow, UnityEngine.InputSystem.Key.DownArrow)) dy = 1;
            if (dx != 0 || dy != 0)
            {
                Move(dx, dy);
                CharmAudio.SelectionChange();
            }

            // 选择/确认（护符界面）：确认 = 跳跃键（默认 W，随键位设置联动），回车作为通用备选
            KeyCode jumpKey = KeyConfig.Parse(
                KnightInCradlePlugin.JumpKey != null
                    ? KnightInCradlePlugin.JumpKey.Value
                    : null, KeyCode.W);
            bool confirm = (RawDown(jumpKey) && jumpKey != KeyCode.Mouse0) ||
                UnityEngine.Input.GetKeyDown(KeyCode.Return);
            if (confirm)
            {
                ConfirmAtCursor();
            }

            // 鼠标支持：指针指向护符图片时选中，左键点击装配/卸下
            Vector2 mouseGui = new Vector2(UnityEngine.Input.mousePosition.x,
                Screen.height - UnityEngine.Input.mousePosition.y);
            if (HitTestMouse(mouseGui, out int hRow, out int hCol))
            {
                bool hitValid = true;
                // GG 按钮模式下：鼠标停在束缚本体（已装备栏）不打断按钮模式，指向按钮/网格护符才切回
                if (_cursorRow == -2 && hRow == 0)
                {
                    hitValid = false;
                }
                if (hitValid && (_cursorRow != hRow || _cursorCol != hCol))
                {
                    MoveCursorTo(hRow, hCol);
                    CharmAudio.SelectionChange();
                }
            }
            if (UnityEngine.Input.GetMouseButtonDown(0) && jumpKey != KeyCode.Mouse0)
            {
                if (HitTestMouse(mouseGui, out int cRow, out int cCol))
                {
                    MoveCursorTo(cRow, cCol); // 点击处可能尚未触发悬停，先同步选中再确认
                    ConfirmAtCursor();
                }
            }

            // T 键（换人键）：格林之子 ↔ 无忧旋律 互换（光标在网格护符上时生效）
            KeyCode toggleKey = KeyConfig.Parse(
                KnightInCradlePlugin.ToggleKey != null
                    ? KnightInCradlePlugin.ToggleKey.Value
                    : null, KeyCode.T);
            if (RawDown(toggleKey) && _cursorRow > 0)
            {
                int cur = CursorGridId;
                int other = cur == CharmEffects.GrimmId
                    ? CharmEffects.MelodyId
                    : cur == CharmEffects.MelodyId ? CharmEffects.GrimmId : -1;
                if (other > 0)
                {
                    int idx = (_cursorRow - 1) * _columns + _cursorCol;
                    if (idx >= 0 && idx < _gridIds.Count)
                    {
                        _gridIds[idx] = other;
                        int eIdx = _equippedIds.IndexOf(cur);
                        if (eIdx >= 0)
                        {
                            _equippedIds[eIdx] = other;
                            RecalcCost();
                            CharmSave.WriteEquipped(); // 装备变化立即写入 SF，随下次存档持久化
                            CharmSave.SyncFromController();
                            CharmEffects.SyncGreedCapacity();
                        }
                        // 变体状态随存档持久化：无忧旋律=43，格林之子=0
                        COOK.setSF(VariantKey, other == CharmEffects.MelodyId
                            ? CharmEffects.MelodyId : 0);
                        RefreshSelection(); // 右侧描述随选中护符一并更新
                        CharmAudio.SelectionChange();
                    }
                }
            }
        }

        /// <summary>鼠标位置（GUI 左上原点）命中测试：命中已装备栏/网格护符图片返回其光标位置。</summary>
        private bool HitTestMouse(Vector2 guiPos, out int row, out int col)
        {
            row = 0;
            col = 0;
            if (_layer == null)
            {
                return false;
            }
            // GG 自限按钮（右侧四锁）：仅当束缚面板显示（选中束缚）时可悬停/点击
            if (SelectedId == CharmDatabase.GgSelectorId)
            {
                for (int b = 0; b < GgButtonKeys.Length; b++)
                {
                    if (_layer.TryGetGgButtonRect(b, out Rect br) && br.Contains(guiPos))
                    {
                        row = -2;
                        col = b;
                        return true;
                    }
                }
            }
            // 已装备栏图标（优先，位于网格上方）
            for (int i = 0; i < _equippedIds.Count; i++)
            {
                Rect r = _layer.GetEquippedIconRect(i, _equippedIds[i]);
                if (r.width > 0f && r.Contains(guiPos))
                {
                    row = 0;
                    col = i;
                    return true;
                }
            }
            // 护符网格图标（含已装备护符对应的槽位，点击可卸下）
            for (int g = 0; g < _gridIds.Count; g++)
            {
                Rect r = _layer.GetGridCharmRect(_gridIds[g]);
                if (r.width > 0f && r.Contains(guiPos))
                {
                    row = g / _columns + 1;
                    col = g % _columns;
                    return true;
                }
            }
            return false;
        }

        /// <summary>对当前光标位置执行确认（键盘回车/确认键与鼠标左键共用）。</summary>
        private void ConfirmAtCursor()
        {
            if (_cursorRow == -1)
            {
                TryClickSign(); // 顶部 sign：走自限点击流程（音效由流程自己播放）
                return;
            }
            if (_cursorRow == -2)
            {
                ToggleGgButton(_cursorCol); // GG 按钮模式：切换 1/2
                return;
            }
            CharmAudio.Confirm();
            if (SelectedId == CharmDatabase.GgSelectorId && _sitting)
            {
                // 只有在椅子上才能选择束缚：点击束缚 → 黄色框平移到最上方按钮
                _cursorRow = -2;
                _cursorCol = 0;
                RefreshSelection();
            }
            else
            {
                TryToggleEquip();
            }
        }

        /// <summary>同时读旧版 Input 与新 Input System（游戏实际使用后者）。</summary>
        private static bool KeyDown(KeyCode legacy, UnityEngine.InputSystem.Key modern)
        {
            if (UnityEngine.Input.GetKeyDown(legacy))
            {
                return true;
            }
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && kb[modern].wasPressedThisFrame;
        }

        /// <summary>原始按键判定（绕过 KeyConfig 屏蔽；鼠标键走 GetMouseButtonDown）。</summary>
        private static bool RawDown(KeyCode k)
        {
            if (k == KeyCode.Mouse0 || k == KeyCode.Mouse1 || k == KeyCode.Mouse2)
            {
                int idx = k == KeyCode.Mouse0 ? 0 : (k == KeyCode.Mouse1 ? 1 : 2);
                return UnityEngine.Input.GetMouseButtonDown(idx);
            }
            return UnityEngine.Input.GetKeyDown(k);
        }

        private void BuildGridIds()
        {
            _gridIds.Clear();
            List<int> ids = _layer.GetGridCharmIds();
            foreach (int id in ids)
            {
                if (id == CharmDatabase.FixedCharmId ||
                    id == CharmDatabase.GgSelectorId ||
                    id == CharmEffects.MelodyId)
                {
                    continue; // 虚空之心固定在上方；寻神者仅经 sign 装配；无忧旋律为格林之子变体槽
                }
                _gridIds.Add(id);
            }
            ApplySavedVariant();
        }

        /// <summary>按本存档保存的变体状态，把格林之子槽位替换为无忧旋律（或还原）。</summary>
        private void ApplySavedVariant()
        {
            bool wantMelody = COOK.getSF(VariantKey) == CharmEffects.MelodyId;
            for (int i = 0; i < _gridIds.Count; i++)
            {
                if (_gridIds[i] == CharmEffects.GrimmId)
                {
                    if (wantMelody)
                    {
                        _gridIds[i] = CharmEffects.MelodyId;
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// 顶部 sign 点击流程（本存档内计数）：
        /// 第 1~3 次：播放 chain_cut；
        /// 第 4 次：播放 gg_victory_core_bling 并显示出 gg_godseeker_mode_selector；
        /// 第 5 次：装备 gg_godseeker_mode_selector（虚空之心右侧，其余护符后移，装备后不可卸下）；
        /// 之后：点击 sign 继续播放 chain_cut。
        /// </summary>
        private void TryClickSign()
        {
            int clicks = COOK.getSF(GgClicksKey) + 1; // 本次点击后的计数
            COOK.setSF(GgClicksKey, clicks);
            if (clicks < 4)
            {
                CharmAudio.ChainCut();
                TriggerSignShake(false); // 小幅度震动
                return;
            }
            if (clicks == 4)
            {
                CharmAudio.GgVictoryCoreBling();
                TriggerSignShake(true);  // 寻神者出现：震动幅度大一些
                TriggerGoldFlash();      // 屏幕四周短时金色闪烁（同回血）
                return;
            }
            // 已解锁：点击 sign 播放 chain_cut；第 5 次点击时装备（一次性，之后保持装备）
            CharmAudio.ChainCut();
            TriggerSignShake(false); // 小幅度震动
            if (!_equippedIds.Contains(CharmDatabase.GgSelectorId))
            {
                // 已装备区容量上限（含虚空之心共 12 格）内才允许装配
                if (_equippedIds.Count >= CharmDatabase.NotchCapacity + 1)
                {
                    return;
                }
                _equippedIds.Insert(1, CharmDatabase.GgSelectorId); // 虚空之心右侧
                _flyIndex = _equippedIds.IndexOf(CharmDatabase.GgSelectorId);
                StartFlight(CharmDatabase.GgSelectorId, true, false, false);
                RecalcCost();
                CharmSave.WriteEquipped(); // 装备变化立即写入 SF，随下次存档持久化
                CharmSave.SyncFromController();
                RefreshSelection();
            }
        }

        /// <summary>sign 点击的画面震动（big=true 幅度更大）。</summary>
        private void TriggerSignShake(bool big)
        {
            _signShake = big ? 10f : 4f; // UI 震动幅度（像素）
            try
            {
                m2d.M2DBase m2dBase = m2d.M2DBase.Instance;
                if (m2dBase != null && m2dBase.Cam != null)
                {
                    m2dBase.Cam.setQuake(big ? 4f : 2f, big ? 12 : 8, big ? 2f : 1f, 0);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>屏幕四周短时金色闪烁（同回血闪光）。</summary>
        private void TriggerGoldFlash()
        {
            try
            {
                KnightEntity k = KnightEntity.Instance;
                if (k != null)
                {
                    k.TriggerGoldScreenFlash();
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// GG 按钮切换（0=骨钉 1=外壳 2=护符 3=灵魂）：1↔2 互换，
        /// 每次点击播放 chain_cut + 屏幕/UI 小震动；四者全为 2 时播放完成庆祝。
        /// </summary>
        private void ToggleGgButton(int idx)
        {
            if (idx < 0 || idx >= GgButtonKeys.Length || !_sitting)
            {
                // 束缚按钮只能在椅子上切换
                return;
            }
            string key = GgButtonKeys[idx];
            int cur = COOK.getSF(key);
            int next = cur == 2 ? 1 : 2;
            COOK.setSF(key, next);
            CharmAudio.ChainCut();
            TriggerSignShake(false); // 屏幕与护符界面小幅度震动
            // 束缚变化后同步钳制血量/灵魂（外壳/灵魂束缚降低上限时立即生效）
            try
            {
                KnightEntity k = KnightEntity.Instance;
                if (k != null)
                {
                    k.ClampHealthToMax();
                    k.ClampSoulToMax();
                }
            }
            catch (Exception)
            {
            }
            if (GetGgAllTwo())
            {
                // 四个全部为 2：每次达成都可重复触发庆祝
                CharmAudio.GgVictoryCoreBling();
                TriggerSignShake(true); // 幅度大一些
                TriggerGoldFlash();     // 屏幕四周短时金色闪烁
            }
        }

        private bool GetGgAllTwo()
        {
            for (int i = 0; i < GgButtonKeys.Length; i++)
            {
                if (COOK.getSF(GgButtonKeys[i]) != 2)
                {
                    return false;
                }
            }
            return true;
        }

        private int GridRowCount
        {
            get { return Mathf.Max(1, (_gridIds.Count + _columns - 1) / _columns); }
        }

        private int UpperSlotCount
        {
            // 满 11 槽或已过载时不显示右侧空槽
            get { return _equippedIds.Count + (TotalCost >= CharmDatabase.NotchCapacity ? 0 : 1); }
        }

        private void Move(int dx, int dy)
        {
            if (_cursorRow == -2)
            {
                if (dx < 0)
                {
                    // 按钮向左：跳到左侧网格对应行的最右护符（按钮 i ↔ 网格第 i+1 行）
                    _cursorRow = _cursorCol + 1;
                    _cursorCol = Mathf.Min(_columns - 1,
                        Mathf.Max(0, _gridIds.Count - 1 - _cursorCol * _columns));
                    RefreshSelection();
                    return;
                }
                // GG 按钮模式：上下在 4 个按钮间移动；上到最顶（骨钉）再上 → 回到束缚
                if (dy < 0 && _cursorCol == 0)
                {
                    _cursorRow = 0;
                    _cursorCol = _equippedIds.IndexOf(CharmDatabase.GgSelectorId);
                    if (_cursorCol < 0)
                    {
                        _cursorCol = 0;
                    }
                }
                else
                {
                    _cursorCol = Mathf.Clamp(_cursorCol + dy, 0, 3);
                }
                RefreshSelection();
                return;
            }
            if (_cursorRow == -1)
            {
                // 自限 sign 行：只有“下”能回到已装备栏的虚空之心；左右/上保持选中 sign
                _cursorCol = 0;
                if (dy > 0)
                {
                    _cursorRow = 0;
                    _cursorCol = 0; // 虚空之心（已装备栏第一位）
                }
                RefreshSelection();
                return;
            }
            if (dy < 0 && _cursorRow == 0)
            {
                // 已装备栏任意护符位置继续往上 → 选中顶部的“sign”图片
                _cursorRow = -1;
                _cursorCol = 0;
            }
            else if (dy > 0 && _cursorRow >= GridRowCount)
            {
                _cursorRow = 0;
            }
            else
            {
                int maxRow = GridRowCount;
                _cursorRow = Mathf.Clamp(_cursorRow + dy, 0, maxRow);
            }

            int rowLen = _cursorRow == 0 ? UpperSlotCount : _columns;
            if (rowLen <= 0)
            {
                rowLen = 1;
            }
            _cursorCol = (_cursorCol + dx + rowLen) % rowLen;
            RefreshSelection();
        }

        private void MoveCursorTo(int row, int col)
        {
            _cursorRow = row;
            _cursorCol = col;
            RefreshSelection();
        }

        private void RefreshSelection()
        {
            int prev = SelectedId;
            if (_cursorRow == -2)
            {
                // GG 按钮模式：仍保持“束缚”选中（右侧面板不关闭）
                SelectedId = CharmDatabase.GgSelectorId;
            }
            else if (_cursorRow == -1)
            {
                // sign 不是护符：仅可选中，不显示护符详情
                SelectedId = -1;
            }
            else if (_cursorRow == 0)
            {
                int slots = UpperSlotCount;
                if (slots <= 0)
                {
                    SelectedId = -1;
                }
                else
                {
                    int idx = Mathf.Clamp(_cursorCol, 0, slots - 1);
                    SelectedId = idx < _equippedIds.Count ? _equippedIds[idx] : -1;
                }
            }
            else
            {
                int idx = (_cursorRow - 1) * _columns + _cursorCol;
                SelectedId = idx >= 0 && idx < _gridIds.Count ? _gridIds[idx] : -1;
            }
            if (SelectedId != prev && SelectedId > 0)
            {
                CharmData cd = CharmDatabase.Get(SelectedId);
            }
        }

        private void TryToggleEquip()
        {
            int id = SelectedId;
            if (id <= 0)
            {
                return;
            }
            if (id == CharmDatabase.FixedCharmId || id == CharmDatabase.GgSelectorId)
            {
                // 虚空之心恒在首位不可卸下；寻神者模式选择器装备后同样不可卸下
                return;
            }
            if (!_sitting)
            {
                return;
            }

            if (_equippedIds.Contains(id))
            {
                // 护符12 坚固贪婪：背包占用超过基础上限（占用了扩容格）时无法卸下
                if (id == CharmEffects.GreedId && !CharmEffects.CanUnequipGreed())
                {
                    return;
                }
                _flyIndex = _equippedIds.IndexOf(id); // 原槽位（卸下后列表已变化，先记录）
                _equippedIds.Remove(id);
                StartFlight(id, false, false, false); // 音效延迟到动画到达网格位置时播放
            }
            else
            {
                CharmData cd = CharmDatabase.Get(id);
                if (cd == null)
                {
                    return;
                }
                int newTotal = TotalCost + cd.Cost;
                if (newTotal > CharmDatabase.NotchCapacity &&
                    (TotalCost >= CharmDatabase.NotchCapacity || Overcharmed))
                {
                    return;
                }
                _equippedIds.Add(id);
                _flyIndex = _equippedIds.IndexOf(id); // 目标槽位
                // 音效延迟到动画到达已装备区时播放（超载播超载音，成功播成功+ui_save）
                StartFlight(id, true, newTotal > CharmDatabase.NotchCapacity,
                    newTotal <= CharmDatabase.NotchCapacity);
            }
            RecalcCost();
            CharmSave.WriteEquipped(); // 装备变化立即写入 SF，随下次存档持久化
            CharmSave.SyncFromController(); // 同步快照，护符效果无需打开 UI 即可生效
            // 护符11 坚固心脏：装配时直接回满血（到新上限）；卸下时血量钳制回上限。
            // 护符28/29 生命血：装配时立即补上生命血（28=+2、29=+4，超上限血条变蓝）；卸下时钳回上限。
            // 护符30 乔尼的祝福：装配时血量=新上限（+4 上限 +4 血，血条恒蓝）。
            KnightEntity k = KnightEntity.Instance;
            if (k != null)
            {
                if (_equippedIds.Contains(CharmEffects.JohnnyId) ||
                    _equippedIds.Contains(CharmEffects.BlueHeart1Id) ||
                    _equippedIds.Contains(CharmEffects.BlueHeart2Id))
                {
                    k.KnightApplyLifeblood();
                }
                else if (_equippedIds.Contains(CharmEffects.HeartId))
                {
                    k.KnightRestoreAll();
                }
                else
                {
                    k.ClampHealthToMax();
                }
            }
            // 护符12 坚固贪婪：背包上限随装配/卸下同步 ±8
            CharmEffects.SyncGreedCapacity();
            // 装卸后立即刷新选中：否则 SelectedId 仍停留在刚卸下的护符上，
            // 已装备列表已缩短/错位，再次按 W 会把同一护符又装回来。
            RefreshSelection();
        }

        /// <summary>开始护符装卸平移动画（0.2s），音效在动画到达时统一播放。</summary>
        private void StartFlight(int id, bool equip, bool overcharm, bool save)
        {
            _flyId = id;
            _flyEquip = equip;
            _flyOvercharm = overcharm;
            _flySave = save;
            _flyTimer = FlyDuration;
        }

        private void RecalcCost()
        {
            int sum = 0;
            foreach (int id in _equippedIds)
            {
                CharmData cd = CharmDatabase.Get(id);
                if (cd != null)
                {
                    sum += cd.Cost;
                }
            }
            TotalCost = sum;
        }
    }
}
