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
