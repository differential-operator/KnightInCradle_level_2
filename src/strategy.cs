// ============================================================================
// 策略备注：用绿框标记判定框（碰撞箱可视化调试）
// 记录时间：2026-08-24（护符 18/19 修长之钉/骄傲印记微调期间）
// 本文件只作备注用途，不含任何可执行代码。
// ============================================================================
//
// 【目的】
// 普攻（平砍/上劈/下劈）的判定框是“矩形 Box + 尖端 Polygon”的组合碰撞体，
// 直接凭常量推算位置容易出错。做法是运行时读取真实碰撞体几何，用绿色线框
// 画到屏幕上，让策划/作者直接看框微调，改完再关闭调试开关。
//
// 【相关位置】（KnightEntity.cs）
// - 调试开关：  private const bool AttackHitboxDebug = true;  （微调完改回 false）
// - 换算常量：  private const float UxToMeshPx = 64f;
// - 网格字段：  _attackDbgMesh / _attackDbgTicket / _attackDbgMat
// - 票据分配：  RebindTicket() 内 if (AttackHitboxDebug) { ... }
// - 票据释放：  ReleaseTicket() 内（destruct mesh、DestroyOne mat、置空）
// - 绘制方法：  KnightPrepareAttackDebugMesh(...)
// - 碰撞体生成：SpawnHitbox() / UpdateHitboxPosition() / DestroyHitbox()
//
// 【核心换算链（必须一条链走到底，不能混坐标系）】
// 世界坐标 → 地图根本地坐标(ux) → 减去骑士锚点(mx,my) → ×64 → mesh px 画线
//
//   Transform mapT = _mp.gameObject.transform;          // 地图根
//   Vector3 lp = mapT.InverseTransformPoint(世界点);     // 得到 ux 单位
//   float dx = (lp.x - mx) * UxToMeshPx;                // mesh px
//   float dy = (lp.y - my) * UxToMeshPx;
//   Tk.Matrix = mapT.localToWorldMatrix *
//               Matrix4x4.Translate(new Vector3(mx, my, 0f));  // 锚定骑士原点
//
// 【坐标系事实（从反编译 AIC 源码确认，Map2d.cs）】
// - CLEN = 28：1 格地图 = 28 地图像素（mesh px）。
// - pixel2ux(x) = pixel2meshx(x) * 0.015625；0.015625 = 1/64。
//   所以 1 ux（骑士本地单位）= 64 mesh px，即 UxToMeshPx = 64。
// - 1 格 = 28 mesh px = 0.4375 ux。
// - 地图 y 向下为正；pixel2uy 会翻转（mesh/ux 里 y 向上为正）。
// - 骑士锚点：mx = _mp.pixel2ux(X * CLEN)，my = _mp.pixel2uy(Y * CLEN)。
//
// 【KnightEntity 的挂载结构（重要坑）】
// - KnightEntity 是一个独立根 GameObject（"KnightEntity"，位于世界原点、缩放 1），
//   并没有挂到地图根下面；所有位置都用 mapT.TransformPoint(...) 换算成世界坐标。
// - _hitboxGo 的父级是小骑士的 transform，但其 transform.position 是“世界坐标”。
//   因此 _hitboxGo.transform.localPosition 实际等于世界坐标（父级在原点缩放 1），
//   不能直接拿它减去 (mx,my)——(mx,my) 是地图本地坐标，两者相减会得到乱码，
//   框会画到屏幕外（曾因此“完全不显示”）。
// - 正确做法：世界点一律先过 mapT.InverseTransformPoint 转回地图本地(ux)，
//   再减 (mx,my)、×64。
//
// 【两次踩坑记录】
// 1. 第一版：自己用常量 + 猜的换算画框 → 位置/符号对不上，框不是实际碰撞箱。
// 2. 第二版：读真实碰撞体，但忘了 ×64 → 框只有实际大小的 1/64，缩成“一个点”。
// 3. 第三版：用 _hitboxGo.transform.localPosition 直接减 (mx,my)（混坐标系）
//    → 乱码，完全不显示。
// 4. 第四版（正确）：世界点 → InverseTransformPoint → 减锚点 → ×64，框与碰撞体严格重合。
//
// 【绘制细节】
// - 矩形：取 BoxCollider2D.bounds 的 min/max 四个世界角点，走上面的换算链画四条边。
// - 尖端：取 PolygonCollider2D.points，每个点先 tipT.TransformPoint(...) 转世界，
//   再走换算链，把三个顶点首尾相连。
// - 线宽：MeshDrawer.Line(x0,y0,x1,y1, 2f)；颜色 new Color(0f,1f,0f,0.9f)。
// - 显示时机：_hitboxGo != null && _attacking（整段挥刀过程都显示，方便观察）。
//
// 【与护符 18/19 的关系】
// - 平砍长度/高度/平移共用 CharmEffects.LongRangeMultiplier() /
//   LongRangeHeightMultiplier() / LongRangeShift()，碰撞箱与渲染箱同源，
//   所以改 CharmEffects 一处，两边同时生效。
// - 单位陷阱（重要，2026-08-24 追加）：
//   坐标轴（“，”键）的刻度就是按每 1 格（= _mp.CLEN mesh px）画的，
//   所以“1 格 = 坐标轴 1 个单位 = 地图格”，三者一致。
//   但判定/渲染框的偏移常量（HitboxOffsetX、SlashFxOffsetX）和护符平移
//   是直接以 ux 加在 mx/my 上的（1 ux = 64 mesh px），而 1 格 = 28 mesh px
//   = 28/64 = 0.4375 ux。若把“格”数值直接填入，屏幕上实际移动 1/0.4375
//   ≈ 2.286 格。因此护符平移先用 CellToUx（× CLEN/64）换算再应用，
//   保证“报多少格就动多少格”。
// - 绿框微调结论（面朝左为例，向右对称）：
//   修长之钉：长度 ×1.15、高度 ×1.10、平移 0.0875 格（外伸）、无拉伸
//   骄傲印记：长度 ×1.25、高度 ×1.15、平移 0.3125 格（外伸）、
//             只向身体方向拉伸 0.2 格（远侧边缘不动）
//   两者叠加：长度 ×1.40、高度 ×1.20、平移 0.5 格（外伸）、
//             只向身体方向拉伸 0.1 格
//   上劈高度向上拉伸：长钉 0.3 / 骄傲 0.5 / 叠加 0.8 格（尖端随上边缘上移）
//   下劈高度向下拉伸：长钉 0.2 / 骄傲 0.3 / 叠加 0.5 格
//   （下劈另有一条无条件的基础“向上拉伸 0.4 格”，二者叠加生效）
// - 拉伸实现要点：拉伸量 S（格）加在碰撞箱/渲染宽上，中心再向身体方向
//   移动 S/2（×CellToUx），即可做到“只拉骑士侧、远侧不动”。
//   碰撞体、渲染、CheckAttackOverlap 兜底三处都要同步应用同一套数值。
//
// 【拉伸碰撞箱的方法（通用套路）】
// 语义：拉伸 = 只动一侧边缘，另一侧（远侧）位置不变。
// 做法分两步，缺一不可：
//   1) 尺寸 +S：BoxCollider2D.size（判定框）与渲染宽/高（MeshDrawer 的 w/h）
//      都加上 S（格；渲染里 S×_mp.CLEN 转 mesh px）。
//   2) 中心向被拉的一侧移动 S/2：判定框中心 hy/hx 与渲染中心 fy/fx
//      都加上 CellToUx(S/2)（格 → ux）。
// 方向符号（ux 中 y 向上为正）：
//   - 向上拉伸：中心 +CellToUx(S/2)；
//   - 向下拉伸：中心 -CellToUx(S/2)；
//   - 向身体方向拉伸（平砍护符，面朝左为例）：hx += _faceDir×CellToUx(S/2)。
// 尖端自动跟随：尖端 PolygonCollider2D 相对盒子中心定义，盒子尺寸 +S、
// 中心位移 S/2 后，挂在被拉边缘上的尖端世界位置正好同步移动，无需改尖端。
// 必改四处，否则会出现“绿框/渲染对、实际判定不对”或反之：
//   SpawnHitbox（碰撞体尺寸）、UpdateHitboxPosition（判定中心）、
//   KnightPrepareFxMesh（渲染中心+尺寸）、CheckAttackOverlap（兜底判定）。
//
// 【收尾】
// 微调满意后：把 AttackHitboxDebug 改回 false，绿框即不再绘制。
// ============================================================================
//
// ============================================================================
// 策略备注（二）：联机特效同步的"素材下标表" → 显式索引表（方案 S）
// 记录时间：2026-09-21
// 状态：**未实施**，先记录备用（先做单机新内容，联机适配阶段再落地）
// ============================================================================
//
// 【0. 先记住两条前提】
// 1) 本体贴图**本来就是按名字传的**：PlayerInfo.caneName = 帧名，远端
//    MultiplayerCompat.LoadKnightFrame(名字) 从自己插件的 assets/hk 里找同名 PNG。
//    只有"特效四边形"（KnightFxSync.Quad.Sprite，int16）走下标。
// 2) 单机完全不碰这张表（见第 2 节），所以单机新增内容不需要管下标。
//
// 【1. 问题：下标表是"扫目录"算出来的，因此会整体位移】
// 表由 KnightFxSync.EnsureTable() 现算（src/KnightFxSync.cs 第 110~186 行），构成是：
//     knight_manifest.json 的全部对象键（实测 834 个）
//   ∪ sheets/nest/fluke_manifest.json 的键
//   ∪ assets/hk/**/*.png 的所有文件名（递归，含 sheets/、fx/ 等所有子目录）
//   ∪ 虚拟键 "knight_spore_dot"
//   最后按 StringComparer.Ordinal 排序 → 下标 0..N-1
// 于是这张表**由目录内容决定**：多一张/少一张 PNG，或改一次清单 JSON，整张表就会整体位移。
// 后果是**静默的**：远端会把自己第 N 号当成本地第 N 号，画出一张"存在但不对"的图；
// 唯一的日志（远端贴图缺失）已在第 18 节调试日志清理中删除。
//
// 实测（2026-09-21，本机）：
//     工程 KnightInCradle/assets/hk   2789 文件 / 2028 PNG / 唯一名 1443 → 表 1459 项
//     安装 KnightInCradle/assets/hk   2005 文件 / 1928 PNG / 唯一名 1368 → 表 1384 项
//     两者相差 77 项。两份**安装**当前逐项一致，所以联机画面正常；
//     但只要做一次"把工程素材全量拷进安装"的部署，或让某一位玩家换一份素材包，就会错位。
//     （deploy.ps1 / build_and_deploy.ps1 都是全量拷贝 assets/hk，所以这个坑迟早会踩到。）
//
// 【2. 为什么单机不受影响（本次"先做单机"的依据）】
// - KnightFxSync 的全部引用点只有两类：KnightEntity.BuildRemoteFxPayloadUncached()
//   （发包，名字→下标 IdOf）与 MultiplayerCompat（收包绘制，下标→名字 NameOf）。
//   这两个入口只在**装了 Kaleidoscopic** 时才被调用（MultiplayerCompat 先扫程序集名再挂补丁）。
// - EnsureTable() 只被 IdOf / NameOf 触发 → 没装联机模组时这张表根本不会被构建。
// - 单机的素材加载/渲染全程按名字：
//     LoadStandaloneSprite(name) → assets/hk/sprites/<name>.png
//     各 LoadXxxAssets()      → assets/hk/sheets/<模块>/sprites/<帧名>.png（读各自的 manifest）
//   没有任何地方枚举目录，所以"目录里多出来的文件"对单机零影响。
// - 单机唯一要遵守的是**名字约定**：PNG 文件名 == manifest 里写的帧名，且放在对应目录。
//   名字对不上/文件缺失时是静默不画（LoadStandaloneSprite 里 File.Exists 失败直接 return），
//   所以新增素材后要确认"帧名、目录、清单"三者一致。
//
// 【3. 方案 S：显式索引表（推荐先做，零协议改动）】
// 把"扫目录算表"换成"读一张固定清单"：
//   新增文件：assets/hk/sprite_index.txt（一行一个精灵名，UTF-8，无 BOM）
//   规则（关键，必须写进注释和文档）：
//     - **只允许在末尾追加**；永不重排、永不删除、永不改名（改名=删+追加）。
//     - 下标 = 行号（0 起）；老下标永远保持有效 → 老客户端不认识新下标时
//       NameOf() 返回 null，收包端直接跳过，**降级而不是错位**。
//     - 生成方式：首次从"当前扫描结果"导出，保证与今天的两份安装逐项一致；
//       之后新增素材时手动 append（或让导出工具 append）。
//   EnsureTable() 改成：文件存在 → 按行读入（跳过空行/以 # 开头的注释行）；
//     文件不存在 → 退回旧的扫目录逻辑（保持向后兼容，不至于一升级就白屏）。
//   自检：启动时打一行
//     [KIC][索引表] N 项 前缀哈希=xxxxxxxx（文件=…）
//   —— 两端这行不一致就说明素材/清单没同步；这是把"静默错位"变成"一眼可查"的关键。
//   代价：无（线上字节完全不变，仍是 int16 下标）。
//
// 【4. 落地步骤（联机适配阶段执行）】
// 1) 生成 sprite_index.txt：用当前 EnsureTable 的扫描结果原样导出（先导出到临时目录核对），
//    与两份安装现有表逐项比对，必须 0 差异；
// 2) 改 KnightFxSync.EnsureTable() 为"文件优先、扫描兜底"，加启动自检日志；
// 3) 更新联机适配文档（KIC_联机适配实现说明 1.4 节）：
//    把"两台机器 assets/hk 文件集必须完全一致"改成"两端 sprite_index.txt 必须一致（前缀相同即兼容）"；
// 4) 跑 tools/FxSyncTest 回归（负载本身没变，应全绿）；
// 5) 两份安装同步部署 sprite_index.txt + 新 DLL，启动日志核对"N 项 + 前缀哈希"一致。
//
// 【5. 备选方案（记录备用，不是现在做）】
// - 方案 N（名字直传）：负载升 v6，把四边形的 int16 下标换成"byte 长度 + UTF-8 名字"，
//   或"每帧名字表 + 局部短 id"。彻底摆脱共享表，缺帧即跳过；代价是每帧最多 +1.2KB
//   （64 个四边形时），且要求两端同版本（v5 拼刀段本来就要求同版本）。
// - 方案 M（会话协商 / 资源流）：握手交换名字集合，或借 Kaleidoscopic 的 MPCC
//   按需把 PNG 拉过来。唯一能彻底解除"两端素材一致"要求的方案，
//   需要联机模组作者开口子（见 docs/联机模组架构建议（回作者三问）.md 第 2、4 节）。
//
// 【6. 涉及的位置】
// - src/KnightFxSync.cs：EnsureTable / IdOf / NameOf / Encode / Read（负载读写）
// - src/KnightEntity.cs：BuildRemoteFxPayloadUncached（发包，名字→下标）
// - src/MultiplayerCompat.cs：CreatePlayerInfoPostfix（写 shieldData）、
//   DrawRemoteKnight / DrawRemoteFx / DrawDive（下标→名字→本地 PNG）
// - docs/KIC_联机适配实现说明（给联机模组作者）.md 1.4 节（素材一致性要求）
// ============================================================================
