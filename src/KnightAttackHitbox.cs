using nel;
using UnityEngine;

namespace KnightInCradle
{
    /// <summary>
    /// 小骑士的攻击判定框：临时 BoxCollider2D(IsTrigger) 的检测脚本。
    /// 碰到 AIC 怪物（按 AIC 内部标签/层/怪物组件判断）时通知 KnightEntity 打印日志。
    /// </summary>
    public class KnightAttackHitbox : MonoBehaviour
    {
        private GameObject _root;

        /// <summary>
        /// 记录判定框根对象（Box 与尖端 Polygon 上的脚本都指向同一根，
        /// 命中时销毁整个判定框，避免只删掉一半）。
        /// </summary>
        public void InitRoot(GameObject root)
        {
            _root = root;
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (other == null)
            {
                return;
            }

            // AIC 内部判定方式：
            // 1) NelEnemy 是 AIC 怪物的基类组件（最可靠兜底）
            // 2) "MoverEn" 是 AIC 的敌人标签（诺艾尔法杖就 CompareTag 这个）
            // 3) "Enemy"/"EnemySelf"/"AttackHitable" 是 AIC 的物理层
            NelEnemy enemy = other.GetComponentInParent<NelEnemy>();
            bool isEnemy =
                enemy != null ||
                other.CompareTag("MoverEn") ||
                other.gameObject.layer == LayerMask.NameToLayer("Enemy") ||
                other.gameObject.layer == LayerMask.NameToLayer("EnemySelf") ||
                other.gameObject.layer == LayerMask.NameToLayer("AttackHitable");

            if (isEnemy)
            {
                KnightEntity.Instance?.NotifyAttackHit(enemy);
                // 触碰到怪物的瞬间就销毁碰撞箱，杜绝“判定结束后残留/下落时鬼畜打怪”；
                // 若未触发本回调，仍由 KnightEntity 的 _attackTimer 兜底销毁
                Destroy(_root != null ? _root : gameObject);
                KnightEntity.Instance?.NotifyHitboxDestroyed();
            }
        }
    }
}
