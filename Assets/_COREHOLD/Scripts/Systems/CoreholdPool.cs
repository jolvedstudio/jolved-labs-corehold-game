using System.Collections.Generic;
using UnityEngine;

namespace Corehold.Systems
{
    /// <summary>
    /// A simple generic object pool. Named CoreholdPool (not ObjectPool) to
    /// avoid ambiguity with UnityEngine.Pool.ObjectPool&lt;T&gt; (GDD §11 note).
    ///
    /// After this system exists, nothing in a gameplay path should call
    /// Instantiate or Destroy — spawning routes through Get()/Release().
    /// </summary>
    public class CoreholdPool<T> where T : Component
    {
        private readonly T _prefab;
        private readonly Transform _parent;
        private readonly Stack<T> _free = new Stack<T>();
        private readonly List<T> _all = new List<T>();

        /// <summary>Number of items currently checked out (active).</summary>
        public int ActiveCount => _all.Count - _free.Count;

        /// <summary>Total number of items ever created by this pool.</summary>
        public int TotalCount => _all.Count;

        public CoreholdPool(T prefab, Transform parent, int prewarm)
        {
            _prefab = prefab;
            _parent = parent;

            for (int i = 0; i < prewarm; i++)
            {
                T item = CreateNew();
                item.gameObject.SetActive(false);
                _free.Push(item);
            }
        }

        /// <summary>Reactivate a free item, or grow the pool if none are free.</summary>
        public T Get()
        {
            T item = _free.Count > 0 ? _free.Pop() : CreateNew();
            item.gameObject.SetActive(true);
            return item;
        }

        /// <summary>Deactivate an item and return it to the free list.</summary>
        public void Release(T item)
        {
            if (item == null)
                return;

            item.gameObject.SetActive(false);
            if (item.transform.parent != _parent)
                item.transform.SetParent(_parent, false);

            _free.Push(item);
        }

        private T CreateNew()
        {
            T item = Object.Instantiate(_prefab, _parent);
            _all.Add(item);
            return item;
        }
    }
}
