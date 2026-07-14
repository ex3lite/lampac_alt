(function () {
  'use strict';
  if (window.selfhost_sync_loaded) return;
  window.selfhost_sync_loaded = true;

  var keys = ['favorite', 'file_view', 'recomends_list', 'online_view', 'torrents_view', 'plugins'];
  var applying = false, revision = 0, mutationCounter = 0, listenerAttached = false, flushTimer = 0;

  function user() { return window.SelfHostedAuth && SelfHostedAuth.user(); }
  function localGet(key) { try { return localStorage.getItem(key) || ''; } catch (e) { return ''; } }
  function localSet(key, value) { try { localStorage.setItem(key, value); } catch (e) {} }
  function dirtyKey() { return 'selfhost_sync_dirty:' + user().id; }
  function readDirty() { try { return JSON.parse(localGet(dirtyKey()) || '{}'); } catch (e) { return {}; } }
  function writeDirty(value) { localSet(dirtyKey(), JSON.stringify(value)); }
  function mutationKey() {
    var random = '';
    mutationCounter++;
    try {
      var bytes = new Uint32Array(4);
      crypto.getRandomValues(bytes);
      random = Array.prototype.map.call(bytes, function (x) { return x.toString(36); }).join('');
    } catch (e) { random = Math.random().toString(36).slice(2); }
    return user().id + ':' + Date.now().toString(36) + ':' + random + ':' + mutationCounter.toString(36);
  }

  function prepareUser(current) {
    var active = localGet('selfhost_sync_user');
    if (active !== current.id) {
      var snapshot = {};
      keys.forEach(function (key) { snapshot[key] = Lampa.Storage.get(key, null); });
      localSet('selfhost_prelogin_snapshot:' + (active || 'legacy'), JSON.stringify(snapshot));
      keys.forEach(function (key) { Lampa.Storage.set(key, key === 'favorite' || key === 'file_view' ? {} : [], true); });
      localSet('selfhost_sync_user', current.id);
    }
    localSet('selfhost_sync_initialized:' + current.id, '1');
  }

  function apply(items) {
    applying = true;
    (items || []).forEach(function (item) {
      if (item.kind !== 'setting' && item.kind !== 'plugin') return;
      var empty = item.key === 'favorite' || item.key === 'file_view' ? {} : [];
      Lampa.Storage.set(item.key, item.deleted ? empty : item.data, true);
    });
    applying = false;
    if (items && items.length) Lampa.Favorite.init();
  }

  function restoreDirty() {
    var dirty = readDirty();
    applying = true;
    Object.keys(dirty).forEach(function (key) { Lampa.Storage.set(key, dirty[key].value, true); });
    applying = false;
  }

  function scheduleFlush(delay) {
    clearTimeout(flushTimer);
    flushTimer = setTimeout(flush, delay || 0);
  }

  function queue(key, value) {
    if (!user()) return;
    var dirty = readDirty();
    dirty[key] = { value: value, idempotencyKey: mutationKey(), attempt: 0, nextAt: 0 };
    writeDirty(dirty);
    scheduleFlush(250);
  }

  function flush() {
    if (!user()) return;
    var dirty = readDirty(), names = Object.keys(dirty), now = Date.now();
    var key = names.find(function (name) { return !dirty[name].nextAt || dirty[name].nextAt <= now; });
    if (!key) {
      var next = names.reduce(function (value, name) { return Math.min(value, dirty[name].nextAt || value); }, now + 60000);
      if (names.length) scheduleFlush(Math.max(500, next - now));
      return;
    }
    var entry = dirty[key];
    SelfHostedAuth.api('/api/v1/sync/item', {
      method: 'PUT',
      body: JSON.stringify({ kind: key === 'plugins' ? 'plugin' : 'setting', key: key, data: entry.value, idempotencyKey: entry.idempotencyKey })
    }).then(function (result) {
      var latest = readDirty();
      if (latest[key] && latest[key].idempotencyKey === entry.idempotencyKey) delete latest[key];
      writeDirty(latest);
      revision = Math.max(revision, result.revision || 0);
      scheduleFlush(0);
    }).catch(function () {
      var latest = readDirty();
      if (!latest[key] || latest[key].idempotencyKey !== entry.idempotencyKey) return scheduleFlush(0);
      latest[key].attempt = (latest[key].attempt || 0) + 1;
      latest[key].nextAt = Date.now() + Math.min(60000, 1000 * Math.pow(2, Math.min(6, latest[key].attempt)));
      writeDirty(latest);
      scheduleFlush(Math.max(500, latest[key].nextAt - Date.now()));
    });
  }

  function poll() {
    if (!user()) return;
    SelfHostedAuth.api('/api/v1/sync/changes?after=' + revision).then(function (data) {
      apply(data.items);
      revision = Math.max(revision, data.revision || 0);
    }).catch(function () {});
  }

  function load() {
    var current = user();
    if (!current) return setTimeout(load, 1000);
    SelfHostedAuth.api('/api/v1/sync/bootstrap').then(function (data) {
      prepareUser(current);
      apply(data.items);
      revision = data.revision || 0;
      restoreDirty();
      if (!listenerAttached) {
        listenerAttached = true;
        Lampa.Storage.listener.follow('change', function (event) {
          if (!applying && keys.indexOf(event.name) >= 0) queue(event.name, event.value);
        });
      }
      scheduleFlush(0);
      setInterval(poll, 4000);
    }).catch(function () { setTimeout(load, 2500); });
  }

  if (window.appready) load();
  else Lampa.Listener.follow('app', function (event) { if (event.type === 'ready') load(); });
})();
