/**
 * schedule.pagination.js
 *
 * Shared infinite-scroll factory for side-panel list modules.
 * Owns the IntersectionObserver and the in-flight guard (_loading).
 * Callers own their own _page / _hasMore state and pass an onLoadMore callback.
 *
 * Usage:
 *   const scroll = schedule.infra.pagination.createInfiniteScroll({
 *     sentinelEl: document.getElementById("mySentinel"),
 *     onLoadMore: async () => {
 *       // fetch next page, append to DOM
 *       // return true if more pages remain, false when done
 *       return _hasMore;
 *     },
 *   });
 *   scroll.observe();   // start watching
 *   scroll.disconnect(); // stop watching (call on tab switch / panel close)
 *
 * Dependencies: none
 * Dependents: schedule.backlog, schedule.tasks-panel
 */
(() => {
  "use strict";

  window.schedule = window.schedule || {};
  window.schedule.infra = window.schedule.infra || {};

  function createInfiniteScroll({ sentinelEl, onLoadMore }) {
    if (!sentinelEl || typeof onLoadMore !== "function") {
      console.warn("schedule.pagination: sentinelEl and onLoadMore are required");
      return { observe: () => {}, disconnect: () => {} };
    }

    let _loading = false;
    let _observer = null;

    function disconnect() {
      if (_observer) {
        _observer.disconnect();
        _observer = null;
      }
    }

    function observe() {
      disconnect();
      _observer = new IntersectionObserver(
        async ([entry]) => {
          if (!entry.isIntersecting || _loading) return;
          _loading = true;
          try {
            const hasMore = await onLoadMore();
            if (!hasMore) disconnect();
          } finally {
            _loading = false;
          }
        },
        { threshold: 0.1 }
      );
      _observer.observe(sentinelEl);
    }

    return { observe, disconnect };
  }

  window.schedule.infra.pagination = { createInfiniteScroll };
})();
