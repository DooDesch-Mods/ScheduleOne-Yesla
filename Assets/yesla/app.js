// Yesla. The left column is the car: its picture in its own paint, where it is, and the remote controls (Summon,
// Honk, Lock, Sentry). The right side has three tabs. Drive: pick a destination on the map or by search, send it to
// the car, then press the engage key in the driver seat; the map has a full-screen mode, and the profile row picks
// how the car drives. Paint: the game's paints, applied to the car at once. Plate: the text on the licence plate.
// Everything comes from yesla.state, yesla.places and yesla.palette; the mod pushes yesla.changed / yesla.places.

var WORLD = 1250.8;      // the map picture in css px at zoom 1
var ZOOM_MIN = 0.4;
var ZOOM_MAX = 2.5;
var ZOOM_START = 1.2;
var WHEEL_STEP = 0.2;
var REACH = 10;          // how close a tap has to land to a place marker, in screen px
var BLUR_GRACE = 200;    // ms the result list stays after the field loses focus, so a click on a result lands

var KIND = {
  quest: 'Quest', property: 'Property', customer: 'Customer', dealer: 'Dealer',
  supplier: 'Supplier', stash: 'Dead drop', vehicle: 'Your car', place: 'Place'
};
var PROFILES = ['chill', 'standard', 'madmax'];
var PROFILE_NAME = { chill: 'Chill', standard: 'Standard', madmax: 'Mad Max' };
var TABS = ['drive', 'paint', 'plate'];

var $ = function (id) { return document.getElementById(id); };

var appEl = $('app');
var viewportEl = $('viewport');
var worldEl = $('world');
var placesEl = $('places');
var resultsEl = $('results');
var tagEl = $('tag');

var state = { image: false, player: null, car: null, dest: null, drive: { state: 'Off', reason: '', key: 'Y', profile: 'standard', parking: 'auto' } };
var places = [];
var palette = [];
var selected = null;     // { type: 'place'|'point', id, label, x, y, meta } - chosen but not sent yet
var query = '';
var searching = false;   // the search field has focus
var tab = 'drive';
var full = false;
var plateDraft = null;   // what the player typed into the plate field, null while untouched
var skipTap = false;
var shownPicture = '';

var tx = 0, ty = 0, zoom = ZOOM_START;

// ---- data ------------------------------------------------------------------------------------------------------

function read() {
  var raw = s1.call('yesla.state', '');
  if (!raw) return;
  try { state = JSON.parse(raw); } catch (e) { s1.log('yesla.state did not parse: ' + e); }
}

function readPlaces() {
  var raw = s1.call('yesla.places', '');
  if (!raw) { places = []; return; }
  try { places = JSON.parse(raw); } catch (e) { s1.log('yesla.places did not parse: ' + e); places = []; }
}

function readPalette() {
  var raw = s1.call('yesla.palette', '');
  if (!raw) return;
  try { palette = JSON.parse(raw); } catch (e) { s1.log('yesla.palette did not parse: ' + e); }
}

function later(fn, ms) {
  if (typeof setTimeout === 'function') setTimeout(fn, ms); else fn();
}

// ---- formatting ------------------------------------------------------------------------------------------------

function metres(m) {
  if (m >= 1000) return (m / 1000).toFixed(1) + ' km';
  return Math.round(m / 10) * 10 + ' m';
}

function driveLine() {
  var d = state.drive || {};
  switch (d.state) {
    case 'Ready': return 'Press ' + d.key + ' in the driver seat.';
    case 'Driving':
      var parts = ['Driving'];
      if (state.car) parts.push(Math.round(state.car.speed) + ' km/h');
      if (state.dest) parts.push(metres(state.dest.distance) + ' left');
      return parts.join(' · ');
    case 'Parking': return 'Parking.';
    case 'Arrived': return (d.reason || '').indexOf('parked') >= 0 ? 'Parked.' : 'Arrived.';
    case 'Aborted': return stopReason(d.reason || '');
    default: return state.dest ? metres(state.dest.distance) + ' away' : '';
  }
}

function stopReason(r) {
  if (r.indexOf('steering') >= 0 || r.indexOf('throttle') >= 0 || r.indexOf('handbrake') >= 0) return 'You took over.';
  if (r.indexOf('no route') >= 0) return 'No route found.';
  if (r.indexOf('left the') >= 0) return 'You left the car.';
  return 'Stopped.';
}

function cleanPlate(raw) {
  var out = '', chars = 0, gap = false;
  var s = (raw || '').toUpperCase();
  for (var i = 0; i < s.length; i++) {
    var ch = s.charAt(i);
    if (ch === ' ') { gap = out.length > 0; continue; }
    if (!((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))) continue;
    if (chars === 8) break;
    if (gap) { out += ' '; gap = false; }
    out += ch;
    chars++;
  }
  return out;
}

function setClass(el, cls) { if (el.className !== cls) el.className = cls; }
function setText(el, text) { if (el.textContent !== text) el.textContent = text; }

// The status line and its dot share one state: '' (quiet), 'live' or 'warn'.
function setStatus(kind) {
  setClass($('car-line'), kind ? 'status ' + kind : 'status');
  setClass($('car-dot'), kind ? 'sdot ' + kind : 'sdot');
}

// A round control and the label under it: the label greys out with the button.
function setTile(id, disabled, on) {
  var b = $(id);
  b.disabled = disabled;
  setClass(b, on ? 'tile on' : 'tile');
  setClass($(id + '-t'), disabled ? 'tl off' : 'tl');
}

function show(el, on) {
  var c = el.className.replace(' hidden', '');
  setClass(el, on ? c : c + ' hidden');
}

function flash(text) {
  tagEl.textContent = text;
  setClass(tagEl, text ? 'tag on' : 'tag');
}

// ---- the map ---------------------------------------------------------------------------------------------------

function view() {
  var r = viewportEl.rect ? viewportEl.rect() : null;
  if (r && r.width > 0 && r.height > 0) return { w: r.width, h: r.height };
  return full ? { w: 733.4, h: 400 } : { w: 434, h: 290 };
}

function centreOn(x, y) {
  var v = view();
  tx = v.w / 2 - x * WORLD * zoom;
  ty = v.h / 2 - y * WORLD * zoom;
  apply();
}

function apply() {
  var v = view();
  if (zoom < ZOOM_MIN) zoom = ZOOM_MIN;
  if (zoom > ZOOM_MAX) zoom = ZOOM_MAX;
  var span = WORLD * zoom;
  tx = Math.min(0, Math.max(v.w - span, tx));
  ty = Math.min(0, Math.max(v.h - span, ty));
  worldEl.style.transform = 'translate(' + tx.toFixed(1) + 'px, ' + ty.toFixed(1) + 'px) scale(' + zoom.toFixed(3) + ')';
}

function zoomAt(next, px, py) {
  if (next < ZOOM_MIN) next = ZOOM_MIN;
  if (next > ZOOM_MAX) next = ZOOM_MAX;
  if (next === zoom) return;
  var wx = (px - tx) / (WORLD * zoom);
  var wy = (py - ty) / (WORLD * zoom);
  zoom = next;
  tx = px - wx * WORLD * zoom;
  ty = py - wy * WORLD * zoom;
  apply();
}

// One style attribute per marker: two style properties re-parse the declaration block twice.
function put(el, x, y, w, h, rot) {
  var s = 'left:' + (x * WORLD - w / 2).toFixed(1) + 'px;top:' + (y * WORLD - h / 2).toFixed(1) + 'px';
  if (rot !== undefined) s += ';transform:rotate(' + Math.round(rot) + 'deg)';
  el.setAttribute('style', s);
}

function renderMarks() {
  setClass($('nomap'), state.image ? 'nomap' : 'nomap on');

  var me = $('m-me');
  show(me, !!state.player && !(state.car && state.car.inside));
  if (state.player) put(me, state.player.x, state.player.y, 22, 22);

  var car = $('m-car');
  show(car, !!state.car);
  if (state.car) put(car, state.car.x, state.car.y, 9, 16, state.car.h);

  var dest = $('m-dest');
  show(dest, !!state.dest);
  if (state.dest) put(dest, state.dest.x, state.dest.y, 16, 16);

  var pend = $('m-pending');
  show(pend, !!selected);
  if (selected) put(pend, selected.x, selected.y, 16, 16);
}

function renderSpots() {
  placesEl.replaceChildren();
  for (var i = 0; i < places.length; i++) {
    var p = places[i];
    var el = document.createElement('div');
    el.className = 'spot ' + p.kind;
    put(el, p.x, p.y, 6, 6);
    placesEl.appendChild(el);
  }
}

function placeAt(px, py) {
  var wx = (px - tx) / (WORLD * zoom);
  var wy = (py - ty) / (WORLD * zoom);
  var reach = REACH / (WORLD * zoom);
  var best = null, bestD = reach * reach;
  for (var i = 0; i < places.length; i++) {
    var dx = places[i].x - wx, dy = places[i].y - wy;
    var d = dx * dx + dy * dy;
    if (d <= bestD) { bestD = d; best = places[i]; }
  }
  return best;
}

// Full screen keeps the point in the middle of the map where it was.
function setFull(on) {
  if (full === on) return;
  var v = view();
  var cx = (v.w / 2 - tx) / (WORLD * zoom);
  var cy = (v.h / 2 - ty) / (WORLD * zoom);
  full = on;
  setClass(appEl, full ? 'app full' : 'app');
  $('full-i').setAttribute('src', full ? 'i-shrink.png' : 'i-expand.png');
  centreOn(cx, cy);
  later(function () { centreOn(cx, cy); }, 0);
}

// ---- search ----------------------------------------------------------------------------------------------------

function renderResults() {
  var open = tab === 'drive' && (searching || query.length > 0);
  show(resultsEl, open);
  if (!open) return;
  resultsEl.replaceChildren();
  var q = query.toLowerCase();
  var shown = 0;
  for (var i = 0; i < places.length; i++) {
    var p = places[i];
    if (q && p.label.toLowerCase().indexOf(q) < 0) continue;
    resultsEl.appendChild(row(p));
    shown++;
  }
  if (shown === 0) {
    var empty = document.createElement('div');
    empty.className = 'empty';
    empty.textContent = q ? 'No place matches.' : 'No places on the map yet.';
    resultsEl.appendChild(empty);
  }
}

function row(p) {
  var el = document.createElement('div');
  el.className = 'row';
  var sw = document.createElement('div');
  sw.className = 'swatch ' + p.kind;
  el.appendChild(sw);
  var text = document.createElement('div');
  text.className = 'text';
  var n = document.createElement('div');
  n.className = 'rn';
  n.textContent = p.label;
  var m = document.createElement('div');
  m.className = 'rm';
  m.textContent = (KIND[p.kind] || 'Place') + ' · ' + metres(p.distance);
  text.appendChild(n);
  text.appendChild(m);
  el.appendChild(text);
  el.addEventListener('click', function () {
    closeSearch();
    selectPlace(p);
    centreOn(p.x, p.y);
  });
  return el;
}

function closeSearch() {
  searching = false;
  query = '';
  var field = $('search');
  if (field.value !== '') field.value = '';
  if (field.blur) field.blur();
  renderResults();
}

// ---- the car column --------------------------------------------------------------------------------------------

function summonLine() {
  var m = state.summon || {};
  switch (m.state) {
    case 'Coming':
      var parts = ['Coming'];
      if (state.car) parts.push(Math.round(state.car.speed) + ' km/h');
      if (state.car) parts.push(metres(state.car.distance) + ' away');
      return parts.join(' · ');
    case 'Arrived': return 'Your car is here.';
    case 'Failed': return m.reason || 'Summon stopped.';
    default: return '';
  }
}

function renderCar() {
  var car = state.car;
  var line = $('car-line');
  var pic = $('car-pic');

  if (!car) {
    setText($('car-name'), 'Yesla');
    setText(line, 'No Yesla nearby');
    setStatus('');
    show(pic, false);
    setText($('s-top'), '-');
    setText($('s-plate'), '-');
  } else {
    setText($('car-name'), car.name);
    if (car.picture && car.picture !== shownPicture) {
      shownPicture = car.picture;
      pic.setAttribute('src', 's1://' + car.picture);
    }
    show(pic, !!car.picture);
    setText($('s-top'), String(Math.round(car.top)));
    setText($('s-plate'), car.plate || '-');

    var driving = state.drive && state.drive.state === 'Driving';
    var summoned = summonLine();
    if (driving) { setText(line, 'Assisted Driving · ' + Math.round(car.speed) + ' km/h'); setStatus('live'); }
    else if (summoned) { setText(line, summoned); setStatus(state.summon.state === 'Failed' ? 'warn' : 'live'); }
    else {
      var parts = [];
      parts.push(car.speed > 2 ? Math.round(car.speed) + ' km/h' : 'Parked');
      if (!car.inside) parts.push(metres(car.distance) + ' away');
      if (car.locked) parts.push('Locked');
      if (car.sentry) parts.push('Sentry');
      setText(line, parts.join(' · '));
      setStatus('');
    }
  }
  setText($('s-profile'), PROFILE_NAME[(state.drive && state.drive.profile) || 'standard'] || 'Standard');
  renderTiles();
}

function renderTiles() {
  var car = state.car;
  var mine = !!(car && car.mine);
  var coming = state.summon && state.summon.state === 'Coming';

  setTile('c-summon', !mine || car.inside, coming);
  setText($('c-summon-t'), coming ? 'Stop' : 'Summon');

  setTile('c-honk', !mine, false);

  var locked = mine && car.locked;
  setTile('c-lock', !mine, locked);
  setText($('c-lock-t'), locked ? 'Locked' : 'Unlocked');
  $('c-lock-i').setAttribute('src', locked ? 'i-lock.png' : 'i-unlock.png');

  setTile('c-sentry', !mine, mine && car.sentry);
}

function control(call, arg) {
  var r = s1.call(call, arg || '');
  if (r !== 'ok') { flash(r && r.indexOf('error:') === 0 ? r.substring(6) : 'The car did not answer.'); return false; }
  flash('');
  read();
  renderAll();
  return true;
}

// ---- tabs ------------------------------------------------------------------------------------------------------

function setTab(id) {
  if (tab === id) return;
  tab = id;
  if (id !== 'drive') { setFull(false); searching = false; }
  renderTabs();
}

function renderTabs() {
  for (var i = 0; i < TABS.length; i++) {
    var id = TABS[i];
    setClass($('t-' + id), id === tab ? 'tab on' : 'tab');
    show($('p-' + id), id === tab);
  }
  renderResults();
  if (tab === 'paint') renderPaint();
  if (tab === 'plate') renderPlate();
}

// ---- drive -----------------------------------------------------------------------------------------------------

function renderProfiles() {
  var current = (state.drive && state.drive.profile) || 'standard';
  for (var i = 0; i < PROFILES.length; i++) {
    var id = PROFILES[i];
    var cls = 'sg';
    if (id === current) cls += id === 'madmax' ? ' on mad' : ' on';
    setClass($('p-' + id), cls);
  }
  var parking = (state.drive && state.drive.parking) || 'auto';
  ['auto', 'reverse', 'forward'].forEach(function (mode) {
    setClass($('k-' + mode), parking === mode ? 'sg on' : 'sg');
  });
}

function renderCard() {
  var name = $('card-name'), line = $('card-line');
  var send = $('send'), clear = $('clear');

  if (selected) {
    setText(name, selected.label);
    setClass(name, 'dn');
    setText(line, selected.meta || '');
    setClass(line, 'dl');
    send.disabled = !(state.car && state.car.mine);
    clear.disabled = false;
    return;
  }
  send.disabled = true;
  if (state.dest) {
    setText(name, state.dest.label);
    setClass(name, 'dn');
    setText(line, driveLine());
    var st = state.drive ? state.drive.state : '';
    setClass(line, st === 'Driving' || st === 'Parking' || st === 'Arrived' ? 'dl live' : (st === 'Aborted' ? 'dl warn' : 'dl'));
    clear.disabled = false;
    return;
  }
  setText(name, 'Tap the map or search a place');
  setClass(name, 'dn quiet');
  setText(line, '');
  clear.disabled = true;
}

function selectPlace(p) {
  selected = { type: 'place', id: p.id, label: p.label, x: p.x, y: p.y, meta: (KIND[p.kind] || 'Place') + ' · ' + metres(p.distance) };
  flash('');
  renderMarks();
  renderCard();
}

function selectPoint(x, y) {
  selected = { type: 'point', id: '', label: 'Dropped pin', x: x, y: y, meta: '' };
  flash('');
  renderMarks();
  renderCard();
}

function send() {
  if (!selected) return;
  var r = selected.type === 'place'
    ? s1.call('yesla.send.place', selected.id)
    : s1.call('yesla.send.point', selected.x.toFixed(5) + ',' + selected.y.toFixed(5));
  if (r !== 'ok') {
    flash(r && r.indexOf('error:') === 0 ? r.substring(6) : 'The car did not take the destination.');
    return;
  }
  selected = null;
  read();
  renderAll();
}

function clear() {
  if (selected) { selected = null; renderMarks(); renderCard(); return; }
  if (s1.call('yesla.clear', '') !== 'ok') { flash('Could not clear the destination.'); return; }
  read();
  renderAll();
}

// ---- paint -----------------------------------------------------------------------------------------------------

function renderPaint() {
  if (palette.length === 0) readPalette();
  var box = $('swatches');
  var current = state.car ? state.car.paint : '';
  var mine = !!(state.car && state.car.mine);
  var currentName = '-';

  if (box.children.length !== palette.length) {
    box.replaceChildren();
    for (var i = 0; i < palette.length; i++) box.appendChild(swatch(palette[i]));
  }
  for (var j = 0; j < palette.length; j++) {
    var el = box.children[j];
    setClass(el, palette[j].id === current ? 'sw on' : 'sw');
    el.disabled = !mine;
    if (palette[j].id === current) currentName = palette[j].name;
  }
  setText($('paint-name'), state.car ? currentName : 'No Yesla nearby');
}

function swatch(c) {
  var el = document.createElement('button');
  el.className = 'sw';
  var disc = document.createElement('div');
  disc.className = 'disc';
  disc.setAttribute('style', 'background:' + c.hex);
  el.appendChild(disc);
  el.addEventListener('click', function () {
    if (control('yesla.paint', c.id) && state.car) { state.car.paint = c.id; renderPaint(); }
  });
  return el;
}

// ---- plate -----------------------------------------------------------------------------------------------------

function renderPlate() {
  var car = state.car;
  var mine = !!(car && car.mine);
  var current = car ? (car.plate || '') : '';
  var draft = plateDraft !== null ? cleanPlate(plateDraft) : '';
  setText($('plate-preview'), draft || current || '-');
  $('plate-apply').disabled = !mine || plateDraft === null || draft === '' || draft === current;
  $('plate-reset').disabled = !mine || !car.plateOwn;
  $('plate-in').disabled = !mine;
  var hint = 'Up to 8 letters and digits.';
  if (car && car.plateDefault) hint += ' Reset gives back ' + car.plateDefault + '.';
  setText($('plate-hint'), hint);
}

function applyPlate() {
  var draft = cleanPlate(plateDraft || '');
  if (!draft) return;
  if (control('yesla.plate', draft)) {
    plateDraft = null;
    var field = $('plate-in');
    field.value = '';
    if (state.car) state.car.plate = draft;
    renderPlate();
  }
}

function resetPlate() {
  if (control('yesla.plate', '')) {
    plateDraft = null;
    $('plate-in').value = '';
    renderPlate();
  }
}

// ---- wiring ----------------------------------------------------------------------------------------------------

function renderAll() {
  renderMarks();
  renderCar();
  renderProfiles();
  renderCard();
  if (tab === 'paint') renderPaint();
  if (tab === 'plate') renderPlate();
}

TABS.forEach(function (id) { $('t-' + id).addEventListener('click', function () { setTab(id); }); });

$('send').addEventListener('click', send);
$('clear').addEventListener('click', clear);
$('c-summon').addEventListener('click', function () { control('yesla.summon'); });
$('c-honk').addEventListener('click', function () { control('yesla.honk'); });
$('c-lock').addEventListener('click', function () { control('yesla.lock'); });
$('c-sentry').addEventListener('click', function () { control('yesla.sentry'); });
PROFILES.forEach(function (id) {
  $('p-' + id).addEventListener('click', function () { control('yesla.profile', id); });
});
['auto', 'reverse', 'forward'].forEach(function (mode) {
  $('k-' + mode).addEventListener('click', function () { control('yesla.parking', mode); });
});

$('search').addEventListener('input', function (e) {
  query = (e.value !== undefined ? e.value : $('search').value) || '';
  renderResults();
});
$('search').addEventListener('focus', function () { searching = true; renderResults(); });
$('search').addEventListener('blur', function () {
  later(function () { searching = false; renderResults(); }, BLUR_GRACE);
});
$('search').addEventListener('keydown', function (e) {
  if (e.key !== 'Enter') return;
  var q = query.toLowerCase();
  for (var i = 0; i < places.length; i++) {
    if (q && places[i].label.toLowerCase().indexOf(q) >= 0) {
      var p = places[i];
      closeSearch();
      selectPlace(p);
      centreOn(p.x, p.y);
      return;
    }
  }
});

$('locate').addEventListener('click', function () {
  skipTap = true;
  var t = state.car || state.player;
  if (t) centreOn(t.x, t.y);
});
$('full').addEventListener('click', function () { skipTap = true; setFull(!full); });

$('plate-in').addEventListener('input', function (e) {
  plateDraft = (e.value !== undefined ? e.value : $('plate-in').value) || '';
  renderPlate();
});
$('plate-in').addEventListener('keydown', function (e) { if (e.key === 'Enter') applyPlate(); });
$('plate-apply').addEventListener('click', applyPlate);
$('plate-reset').addEventListener('click', resetPlate);

viewportEl.addEventListener('click', function (e) {
  if (skipTap) { skipTap = false; return; }
  if (searching || query) { closeSearch(); return; }
  if (typeof e.offsetX !== 'number') return;
  var hit = placeAt(e.offsetX, e.offsetY);
  if (hit) { selectPlace(hit); return; }
  selectPoint((e.offsetX - tx) / (WORLD * zoom), (e.offsetY - ty) / (WORLD * zoom));
});

viewportEl.addEventListener('drag', function (e) {
  if (!e.deltaX && !e.deltaY) return;
  tx += e.deltaX;
  ty += e.deltaY;
  apply();
});

viewportEl.addEventListener('wheel', function (e) {
  var v = view();
  var px = typeof e.offsetX === 'number' ? e.offsetX : v.w / 2;
  var py = typeof e.offsetY === 'number' ? e.offsetY : v.h / 2;
  zoomAt(zoom + (e.wheelDelta > 0 ? -WHEEL_STEP : WHEEL_STEP), px, py);
});

// Back steps out one level at a time: the result list, full screen, an unsent selection, then the app.
document.addEventListener('back', function (e) {
  if (searching || query) { e.preventDefault(); closeSearch(); return; }
  if (full) { e.preventDefault(); setFull(false); return; }
  if (selected) { e.preventDefault(); selected = null; renderMarks(); renderCard(); }
});

s1.on('yesla.changed', function () {
  read();
  renderAll();
});

s1.on('yesla.places', function () {
  readPlaces();
  renderSpots();
  renderResults();
});

read();
readPlaces();
readPalette();
renderSpots();
renderTabs();
renderAll();
var start = state.car || state.player;
if (start) { zoom = ZOOM_START; centreOn(start.x, start.y); } else apply();
