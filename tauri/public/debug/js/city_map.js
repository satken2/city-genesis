/**
 * CityMap - 再利用可能な都市マップ描画モジュール
 *
 * Catppuccin Mocha テーマのダークカラーでキャンバス上に都市データを描画する。
 * パン（慣性付き）・ズーム・レイヤー管理を内包し、
 * フック機構でページ固有のマウス操作や描画を注入できる。
 *
 * 使い方:
 *   const map = new CityMap(document.getElementById('container'), {
 *     layers: [
 *       { id: 'zones', url: '/api/v1/zones', key: 'zones', color: '#89b4fa', type: 'polygon', fill: true },
 *       { id: 'paths', url: '/api/v1/paths', key: 'paths', color: '#fab387', type: 'path' },
 *       { id: 'nodes', url: '/api/v1/nodes/all', key: 'nodes', color: '#f9e2af', type: 'node' },
 *     ],
 *   });
 *   map.loadAll().then(() => map.resetView());
 *
 * ※将来的にTauriアプリに移植することを前提とするため、
 *   DOMのグローバル参照やページ固有の状態に依存しないよう設計している。
 */

/* eslint-disable no-unused-vars */

class CityMap {
  /**
   * @param {HTMLElement} container - マップを描画するコンテナ要素（position: relative 推奨）
   * @param {Object} options
   * @param {Array}   options.layers  - レイヤー設定配列
   * @param {string}  options.bgColor - 背景色 (デフォルト: #181825)
   */
  constructor(container, options = {}) {
    this.container = container;
    this.bgColor = options.bgColor || '#181825';

    /* ---- 内部状態 ---- */
    this.state = {
      offsetX: 0,
      offsetY: 0,
      zoom: 1,
      dragging: false,
      dragStartX: 0,
      dragStartY: 0,
      dragOffsetX: 0,
      dragOffsetY: 0,
      canvasW: 0,
      canvasH: 0,
    };

    /* ---- レイヤー管理 ---- */
    this.layers = [];

    /* ---- フック（ページ側で上書きして使用） ---- */
    this.hooks = {
      /**
       * マウス押下時に呼ばれる。true を返すとマップのパンを抑制する。
       * @param {number} wx - ワールドX座標
       * @param {number} wz - ワールドZ座標
       * @param {MouseEvent} e
       * @returns {boolean|undefined}
       */
      onMouseDown: null,

      /**
       * マウス移動時に呼ばれる（フックがアクティブな間のみ）。
       */
      onMouseMove: null,

      /**
       * マウス解放時に呼ばれる（フックがアクティブな間のみ）。
       */
      onMouseUp: null,

      /**
       * 左クリック（ドラッグなし）時に呼ばれる。
       */
      onLeftClick: null,

      /**
       * 右クリック時に呼ばれる。
       */
      onRightClick: null,

      /**
       * マウスホイール時に呼ばれる。true を返すとズームを抑制する。
       */
      onWheel: null,

      /**
       * 全レイヤー描画後に呼ばれる。カスタム描画に使用する。
       * @param {CanvasRenderingContext2D} ctx
       * @param {Object} state
       * @param {CityMap} map
       */
      onDraw: null,

      /**
       * マウスがキャンバス上を移動したときに常に呼ばれる（パン中も含む）。
       * 座標表示などのUI更新に使用する。
       */
      onHover: null,
    };

    /* ---- 慣性スクロール用 ---- */
    this._inertiaVx = 0;
    this._inertiaVy = 0;
    this._inertiaRaf = null;
    this._velBuf = [];
    this._hookActive = false;

    /* ---- キャンバス作成 ---- */
    this.canvas = document.createElement('canvas');
    this.canvas.style.cssText = 'display:block;width:100%;height:100%;cursor:grab;';
    container.appendChild(this.canvas);
    this.ctx = this.canvas.getContext('2d');

    /* ---- レイヤー初期化 ---- */
    if (options.layers) {
      options.layers.forEach(function(cfg) { this.addLayer(cfg); }.bind(this));
    }

    /* ---- イベント設定 ---- */
    this._setupEvents();
    this.resizeCanvas();

    this._resizeHandler = function() { this.resizeCanvas(); }.bind(this);
    window.addEventListener('resize', this._resizeHandler);
  }

  /* ================================================================
   * ライフサイクル
   * ================================================================ */

  /** インスタンスを破棄し、DOMリスナーを解除する */
  destroy() {
    window.removeEventListener('resize', this._resizeHandler);
    if (this._inertiaRaf) cancelAnimationFrame(this._inertiaRaf);
    if (this.canvas.parentNode) this.canvas.parentNode.removeChild(this.canvas);
  }

  /* ================================================================
   * レイヤー管理
   * ================================================================ */

  /**
   * レイヤーを追加する
   * @param {Object} config
   * @param {string} config.id     - レイヤーID（一意）
   * @param {string} config.url    - データ取得API URL
   * @param {string} config.key    - レスポンスJSON内の配列キー
   * @param {string} config.color  - 描画色
   * @param {string} config.type   - 'polygon' | 'path' | 'node'
   * @param {boolean} config.fill  - ポリゴン塗りつぶし（polygonのみ）
   * @param {number} config.lineWidth - 線幅（pathのみ）
   * @param {boolean} config.visible - 初期表示状態
   */
  addLayer(config) {
    var layer = {
      id: config.id,
      url: config.url || '',
      key: config.key || config.id,
      color: config.color || '#cdd6f4',
      type: config.type || 'polygon',
      fill: !!config.fill,
      lineWidth: config.lineWidth || 1,
      visible: config.visible !== false,
      data: [],
    };
    this.layers.push(layer);
    return layer;
  }

  /** レイヤーの表示/非表示を切り替える */
  setLayerVisible(id, visible) {
    var l = this._findLayer(id);
    if (l) { l.visible = visible; this.draw(); }
  }

  /** レイヤーの色を変更する */
  setLayerColor(id, color) {
    var l = this._findLayer(id);
    if (l) { l.color = color; this.draw(); }
  }

  /** レイヤーのデータをAPIから取得する */
  loadLayer(id) {
    var l = this._findLayer(id);
    if (!l || !l.url) return Promise.resolve();
    var self = this;
    return fetch(l.url)
      .then(function(r) { return r.json(); })
      .then(function(d) { l.data = d[l.key] || []; })
      .catch(function(err) { console.warn('Failed to load layer:', id, err); l.data = []; });
  }

  /** 全レイヤーのデータを取得する */
  loadAll() {
    var self = this;
    return Promise.all(this.layers.map(function(l) { return self.loadLayer(l.id); }));
  }

  _findLayer(id) {
    for (var i = 0; i < this.layers.length; i++) {
      if (this.layers[i].id === id) return this.layers[i];
    }
    return null;
  }

  /* ================================================================
   * キャンバス・座標系
   * ================================================================ */

  /** キャンバスサイズをコンテナに合わせる */
  resizeCanvas() {
    var dpr = window.devicePixelRatio || 1;
    this.state.canvasW = this.container.clientWidth;
    this.state.canvasH = this.container.clientHeight;
    this.canvas.width = this.state.canvasW * dpr;
    this.canvas.height = this.state.canvasH * dpr;
    this.canvas.style.width = this.state.canvasW + 'px';
    this.canvas.style.height = this.state.canvasH + 'px';
    this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    this.draw();
  }

  /** ワールド座標 → スクリーン座標 */
  worldToScreen(wx, wz) {
    return [
      (wx - this.state.offsetX) * this.state.zoom,
      (wz - this.state.offsetY) * this.state.zoom,
    ];
  }

  /** スクリーン座標 → ワールド座標 */
  screenToWorld(sx, sz) {
    return [
      sx / this.state.zoom + this.state.offsetX,
      sz / this.state.zoom + this.state.offsetY,
    ];
  }

  /** 現在のビューポートのワールド座標範囲を取得 */
  getViewBounds() {
    var s = this.state;
    var a = this.screenToWorld(0, 0);
    var b = this.screenToWorld(s.canvasW, s.canvasH);
    return {
      x1: Math.min(a[0], b[0]),
      z1: Math.min(a[1], b[1]),
      x2: Math.max(a[0], b[0]),
      z2: Math.max(a[1], b[1]),
    };
  }

  /* ================================================================
   * 描画
   * ================================================================ */

  /** 全レイヤーを再描画する */
  draw() {
    var s = this.state;
    var ctx = this.ctx;
    ctx.clearRect(0, 0, s.canvasW, s.canvasH);
    ctx.fillStyle = this.bgColor;
    ctx.fillRect(0, 0, s.canvasW, s.canvasH);

    var bounds = this.getViewBounds();

    for (var i = 0; i < this.layers.length; i++) {
      var layer = this.layers[i];
      if (!layer.visible || !layer.data || !layer.data.length) continue;

      if (layer.type === 'node') {
        this._drawNodes(layer, bounds);
      } else if (layer.type === 'path') {
        this._drawPaths(layer, bounds);
      } else {
        this._drawPolygons(layer, bounds);
      }
    }

    /* カスタム描画フック */
    if (this.hooks.onDraw) this.hooks.onDraw(ctx, s, this);
  }

  /** polygon レイヤーの描画 */
  _drawPolygons(layer, bounds) {
    var ctx = this.ctx;
    var s = this.state;
    var items = Array.isArray(layer.data) ? layer.data : Object.values(layer.data);

    for (var i = 0; i < items.length; i++) {
      var item = items[i];
      var poly = item.polygon;
      if (!poly || poly.length < 2) continue;

      var bx1 = Infinity, bz1 = Infinity, bx2 = -Infinity, bz2 = -Infinity;
      for (var j = 0; j < poly.length; j++) {
        if (poly[j][0] < bx1) bx1 = poly[j][0];
        if (poly[j][1] < bz1) bz1 = poly[j][1];
        if (poly[j][0] > bx2) bx2 = poly[j][0];
        if (poly[j][1] > bz2) bz2 = poly[j][1];
      }
      if (!this._bboxInView(bx1, bz1, bx2, bz2, bounds)) continue;

      var path = new Path2D();
      var sp = this.worldToScreen(poly[0][0], poly[0][1]);
      path.moveTo(sp[0], sp[1]);
      for (var k = 1; k < poly.length; k++) {
        sp = this.worldToScreen(poly[k][0], poly[k][1]);
        path.lineTo(sp[0], sp[1]);
      }
      path.closePath();

      if (layer.fill) {
        ctx.globalAlpha = 0.25;
        ctx.fillStyle = layer.color;
        ctx.fill(path);
        ctx.globalAlpha = 1.0;
      }
      ctx.strokeStyle = layer.color;
      ctx.lineWidth = 1;
      ctx.stroke(path);

      if (item.center_point) {
        var lp = this.worldToScreen(item.center_point[0], item.center_point[1]);
        if (lp[0] >= -50 && lp[0] <= s.canvasW + 50 && lp[1] >= -50 && lp[1] <= s.canvasH + 50) {
          var label = item.name || item.zone_id || item.parcel_id || item.resource_type_label || '';
          if (label) {
            ctx.font = '11px Consolas, Menlo, monospace';
            ctx.fillStyle = layer.color;
            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            ctx.fillText(String(label), lp[0], lp[1]);
          }
        }
      }
    }
  }

  /** path レイヤーの描画 */
  _drawPaths(layer, bounds) {
    var ctx = this.ctx;
    var s = this.state;
    var items = Array.isArray(layer.data) ? layer.data : Object.values(layer.data);

    for (var i = 0; i < items.length; i++) {
      var item = items[i];
      if (!item.start_point || !item.end_point) continue;

      var pts = this._collectPathPoints(item);
      var bx1 = Infinity, bz1 = Infinity, bx2 = -Infinity, bz2 = -Infinity;
      for (var j = 0; j < pts.length; j++) {
        if (pts[j][0] < bx1) bx1 = pts[j][0];
        if (pts[j][1] < bz1) bz1 = pts[j][1];
        if (pts[j][0] > bx2) bx2 = pts[j][0];
        if (pts[j][1] > bz2) bz2 = pts[j][1];
      }
      if (!this._bboxInView(bx1, bz1, bx2, bz2, bounds)) continue;

      ctx.strokeStyle = layer.color;
      ctx.lineWidth = layer.lineWidth || 2;
      ctx.lineJoin = 'round';
      ctx.lineCap = 'round';
      ctx.beginPath();
      var sp = this.worldToScreen(pts[0][0], pts[0][1]);
      ctx.moveTo(sp[0], sp[1]);
      for (var k = 1; k < pts.length; k++) {
        sp = this.worldToScreen(pts[k][0], pts[k][1]);
        ctx.lineTo(sp[0], sp[1]);
      }
      ctx.stroke();

      if (item.center_point) {
        var lp = this.worldToScreen(item.center_point[0], item.center_point[1]);
        if (lp[0] >= -50 && lp[0] <= s.canvasW + 50 && lp[1] >= -50 && lp[1] <= s.canvasH + 50) {
          var label = item.name || '';
          if (label) {
            ctx.font = '11px Consolas, Menlo, monospace';
            ctx.fillStyle = layer.color;
            ctx.textAlign = 'left';
            ctx.textBaseline = 'bottom';
            ctx.fillText(label, lp[0] + 4, lp[1] - 4);
          }
        }
      }
    }
  }

  /** node レイヤーの描画 */
  _drawNodes(layer, bounds) {
    var ctx = this.ctx;
    var entries = Array.isArray(layer.data) ? layer.data : Object.values(layer.data);
    for (var i = 0; i < entries.length; i++) {
      var node = entries[i];
      if (!node.position) continue;
      var wx = node.position[0];
      var wz = node.position[2];
      if (!this._inView(wx, wz, bounds)) continue;

      var sp = this.worldToScreen(wx, wz);
      ctx.fillStyle = layer.color;
      ctx.beginPath();
      ctx.arc(sp[0], sp[1], 3, 0, Math.PI * 2);
      ctx.fill();
    }
  }

  /**
   * pathアイテムから全ポイントを収集する。
   * 3D座標 [x, y, z] の場合は y(標高)を捨てて [x, z] に変換する。
   */
  _collectPathPoints(item) {
    var pts = [];
    var raw = [item.start_point];
    if (item.waypoints && item.waypoints.length) {
      for (var i = 0; i < item.waypoints.length; i++) {
        var wp = item.waypoints[i];
        raw.push(Array.isArray(wp) ? wp : (wp && wp.position));
      }
    }
    raw.push(item.end_point);
    for (var j = 0; j < raw.length; j++) {
      var p = raw[j];
      if (!p) continue;
      pts.push(p.length >= 3 ? [p[0], p[2]] : [p[0], p[1]]);
    }
    return pts;
  }

  _inView(cx, cz, b) {
    return cx >= b.x1 && cx <= b.x2 && cz >= b.z1 && cz <= b.z2;
  }

  _bboxInView(bx1, bz1, bx2, bz2, b) {
    return !(bx2 < b.x1 || bx1 > b.x2 || bz2 < b.z1 || bz1 > b.z2);
  }

  /* ================================================================
   * ビューリセット
   * ================================================================ */

  /** 全データが収まるようにビューポートを調整する */
  resetView() {
    var s = this.state;
    var minX = Infinity, minZ = Infinity, maxX = -Infinity, maxZ = -Infinity;
    var hasData = false;

    for (var li = 0; li < this.layers.length; li++) {
      var layer = this.layers[li];
      if (!layer.visible) continue;
      var items = layer.data;
      if (!items) continue;
      var arr = Array.isArray(items) ? items : Object.values(items);

      for (var i = 0; i < arr.length; i++) {
        var item = arr[i];
        if (layer.type === 'node') {
          if (item.position) {
            hasData = true;
            var nx = item.position[0], nz = item.position[2];
            if (nx < minX) minX = nx;
            if (nz < minZ) minZ = nz;
            if (nx > maxX) maxX = nx;
            if (nz > maxZ) maxZ = nz;
          }
        } else if (layer.type === 'path') {
          if (item.start_point && item.end_point) {
            hasData = true;
            var pts = this._collectPathPoints(item);
            for (var j = 0; j < pts.length; j++) {
              if (pts[j][0] < minX) minX = pts[j][0];
              if (pts[j][1] < minZ) minZ = pts[j][1];
              if (pts[j][0] > maxX) maxX = pts[j][0];
              if (pts[j][1] > maxZ) maxZ = pts[j][1];
            }
          }
        } else {
          var poly = item.polygon;
          if (poly && poly.length >= 2) {
            hasData = true;
            for (var k = 0; k < poly.length; k++) {
              if (poly[k][0] < minX) minX = poly[k][0];
              if (poly[k][1] < minZ) minZ = poly[k][1];
              if (poly[k][0] > maxX) maxX = poly[k][0];
              if (poly[k][1] > maxZ) maxZ = poly[k][1];
            }
          } else if (item.center_point) {
            hasData = true;
            if (item.center_point[0] < minX) minX = item.center_point[0];
            if (item.center_point[1] < minZ) minZ = item.center_point[1];
            if (item.center_point[0] > maxX) maxX = item.center_point[0];
            if (item.center_point[1] > maxZ) maxZ = item.center_point[1];
          }
        }
      }
    }

    if (!hasData) {
      s.offsetX = 0;
      s.offsetY = 0;
      s.zoom = 1;
      this.draw();
      return;
    }

    var pad = 20;
    var dataW = (maxX - minX) || 1;
    var dataH = (maxZ - minZ) || 1;
    var scaleX = (s.canvasW - pad * 2) / dataW;
    var scaleY = (s.canvasH - pad * 2) / dataH;
    s.zoom = Math.min(scaleX, scaleY);
    s.zoom = Math.max(0.01, Math.min(1000, s.zoom));
    s.offsetX = minX - ((s.canvasW / s.zoom) - dataW) / 2;
    s.offsetY = minZ - ((s.canvasH / s.zoom) - dataH) / 2;
    this.draw();
  }

  /* ================================================================
   * イベント処理
   * ================================================================ */

  _setupEvents() {
    var self = this;
    var cv = this.canvas;
    var INERTIA_FRICTION = 0.92;
    var INERTIA_THRESHOLD = 0.1;
    var VEL_BUF_SIZE = 4;
    var CLICK_THRESHOLD = 5;

    /* ---- マウス押下 ---- */
    cv.addEventListener('mousedown', function(e) {
      if (e.button !== 0) return;

      var rect = cv.getBoundingClientRect();
      var sx = e.clientX - rect.left;
      var sz = e.clientY - rect.top;
      var wc = self.screenToWorld(sx, sz);

      /* フック確認 */
      if (self.hooks.onMouseDown && self.hooks.onMouseDown(wc[0], wc[1], e)) {
        self._hookActive = true;
        return;
      }

      /* パン開始 */
      self.state.dragging = true;
      self.state.dragStartX = e.clientX;
      self.state.dragStartY = e.clientY;
      self.state.dragOffsetX = self.state.offsetX;
      self.state.dragOffsetY = self.state.offsetY;
      self._inertiaVx = 0;
      self._inertiaVy = 0;
      self._velBuf.length = 0;
      if (self._inertiaRaf) { cancelAnimationFrame(self._inertiaRaf); self._inertiaRaf = null; }
    });

    /* ---- マウス移動 ---- */
    window.addEventListener('mousemove', function(e) {
      var rect = cv.getBoundingClientRect();
      var sx = e.clientX - rect.left;
      var sz = e.clientY - rect.top;
      var wc = self.screenToWorld(sx, sz);

      /* ホバーフック（常に呼ぶ） */
      if (sx >= 0 && sx <= self.state.canvasW && sz >= 0 && sz <= self.state.canvasH) {
        if (self.hooks.onHover) self.hooks.onHover(wc[0], wc[1], e);
      }

      /* フックがアクティブなら委譲 */
      if (self._hookActive) {
        if (self.hooks.onMouseMove) self.hooks.onMouseMove(wc[0], wc[1], e);
        return;
      }

      if (!self.state.dragging) return;

      var dx = e.clientX - self.state.dragStartX;
      var dy = e.clientY - self.state.dragStartY;
      var newOffX = self.state.dragOffsetX - dx / self.state.zoom;
      var newOffY = self.state.dragOffsetY - dy / self.state.zoom;

      self._velBuf.push({
        vx: newOffX - self.state.offsetX,
        vy: newOffY - self.state.offsetY,
      });
      if (self._velBuf.length > VEL_BUF_SIZE) self._velBuf.shift();

      self.state.offsetX = newOffX;
      self.state.offsetY = newOffY;
      self.draw();
    });

    /* ---- マウス解放 ---- */
    window.addEventListener('mouseup', function(e) {
      var rect = cv.getBoundingClientRect();
      var sx = e.clientX - rect.left;
      var sz = e.clientY - rect.top;
      var wc = self.screenToWorld(sx, sz);

      /* フックがアクティブなら委譲して終了 */
      if (self._hookActive) {
        if (self.hooks.onMouseUp) self.hooks.onMouseUp(wc[0], wc[1], e);
        self._hookActive = false;
        return;
      }

      if (!self.state.dragging) return;
      self.state.dragging = false;

      /* クリック判定（移動距離が閾値未満ならクリックとみなす） */
      var dx = e.clientX - self.state.dragStartX;
      var dy = e.clientY - self.state.dragStartY;
      if (Math.abs(dx) < CLICK_THRESHOLD && Math.abs(dy) < CLICK_THRESHOLD) {
        if (self.hooks.onLeftClick) self.hooks.onLeftClick(wc[0], wc[1], e);
        return;
      }

      /* 慣性スクロール開始 */
      if (self._velBuf.length >= 2) {
        var recent = self._velBuf[self._velBuf.length - 1];
        self._inertiaVx = recent.vx;
        self._inertiaVy = recent.vy;
      }
      if (Math.abs(self._inertiaVx) > INERTIA_THRESHOLD || Math.abs(self._inertiaVy) > INERTIA_THRESHOLD) {
        startInertia();
      }
    });

    function startInertia() {
      function tick() {
        self._inertiaVx *= INERTIA_FRICTION;
        self._inertiaVy *= INERTIA_FRICTION;
        if (Math.abs(self._inertiaVx) < INERTIA_THRESHOLD && Math.abs(self._inertiaVy) < INERTIA_THRESHOLD) {
          self._inertiaRaf = null;
          return;
        }
        self.state.offsetX += self._inertiaVx;
        self.state.offsetY += self._inertiaVy;
        self.draw();
        self._inertiaRaf = requestAnimationFrame(tick);
      }
      self._inertiaRaf = requestAnimationFrame(tick);
    }

    /* ---- マウスホイール（ズーム） ---- */
    cv.addEventListener('wheel', function(e) {
      e.preventDefault();
      var rect = cv.getBoundingClientRect();
      var sx = e.clientX - rect.left;
      var sz = e.clientY - rect.top;

      if (self.hooks.onWheel && self.hooks.onWheel(e)) return;

      var wBefore = self.screenToWorld(sx, sz);

      var factor = e.deltaY < 0 ? 1.15 : 1 / 1.15;
      self.state.zoom = Math.max(0.01, Math.min(1000, self.state.zoom * factor));

      var wAfter = self.screenToWorld(sx, sz);
      self.state.offsetX += wBefore[0] - wAfter[0];
      self.state.offsetY += wBefore[1] - wAfter[1];

      self.draw();
    }, { passive: false });

    /* ---- 右クリック ---- */
    cv.addEventListener('contextmenu', function(e) {
      e.preventDefault();
      var rect = cv.getBoundingClientRect();
      var sx = e.clientX - rect.left;
      var sz = e.clientY - rect.top;
      var wc = self.screenToWorld(sx, sz);
      if (self.hooks.onRightClick) self.hooks.onRightClick(wc[0], wc[1], e);
    });
  }
}
