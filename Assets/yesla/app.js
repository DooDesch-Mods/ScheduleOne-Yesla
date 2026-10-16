// Yesla. Pick a destination on the map or in the list, send it to the car, then press the engage key in the driver
// seat. Everything the page shows comes from two calls: yesla.state (player, car, destination, drive state) and
// yesla.places (the map's markers). The mod pushes yesla.changed / yesla.places when either changes.

var WORLD = 1250.8;      // the map picture in css px at zoom 1
var ZOOM_MIN = 0.4;
var ZOOM_MAX = 2.5;
var ZOOM_START = 1.2;
var WHEEL_STEP = 0.2;
var SIDE = 263;          // .side width plus its border, for the fallback window size
var REACH = 10;          // how close a tap has to land to a place marker, in screen px

var KIND = {
  quest: 'Quest', property: 'Property', customer: 'Customer', dealer: 'Dealer',
  supplier: 'Supplier', stash: 'Dead drop', vehicle: 'Your car', place: 'Place'
};

var $ = function (id) { return document.getElementById(id); };

var viewportEl = $('viewport');
var worldEl = $('world');
var placesEl = $('places');
var listEl = $('list');
var tagEl = $('tag');

var state = { image: false, player: null, car: null, dest: null, drive: { state: 'Off', reason: '', key: 'Y' } };
var places = [];
var selected = null;     // { type: 'place'|'point', id, label, x, y } - chosen but not sent yet
var query = '';
var skipTap = false;

var tx = 0, ty = 0, zoom = ZOOM_START, placed = false;

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
    case 'Arrived': return 'Arrived.';
    case 'Aborted': return stopReason(d.reason || '');
    default: return '';
  }
}

function stopReason(r) {
  if (r.indexOf('steering') >= 0 || r.indexOf('throttle') >= 0 || r.indexOf('handbrake') >= 0) return 'You took over.';
  if (r.indexOf('no route') >= 0) return 'No route found.';
  if (r.indexOf('left the') >= 0) return 'You left the car.';
  if (r.indexOf('engage key') >= 0) return 'Stopped.';
  return 'Stopped.';
}

// ---- the map ---------------------------------------------------------------------------------------------------

function view() {
  var r = viewportEl.rect ? viewportEl.rect() : null;
  if (r && r.width > 0 && r.height > 0) return { w: r.width, h: r.height };
  if (typeof s1 !== 'undefined' && s1.screen && s1.screen.w > 0) return { w: s1.screen.w - SIDE, h: s1.screen.h };
  return { w: 733.4 - SIDE, h: 400 };
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

function show(el, on) {
  var c = el.className.replace(' hidden', '');
  el.className = on ? c : c + ' hidden';
}

function renderMarks() {
  $('nomap').className = state.image ? 'nomap' : 'nomap on';

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

function flash(text) {
  tagEl.textContent = text;
  tagEl.className = text ? 'tag on' : 'tag';
}

// ---- side column -----------------------------------------------------------------------------------------------

function renderHead() {
  var line = $('car-line');
  if (!state.car) {
    $('car-name').textContent = 'Yesla';
    line.textContent = 'No Yesla nearby';
    line.className = 'sub';
    return;
  }
  $('car-name').textContent = state.car.name;
  var driving = state.drive && state.drive.state === 'Driving';
  if (driving) { line.textContent = 'Assisted Driving'; line.className = 'sub live'; return; }
  line.className = 'sub';
  if (state.car.inside) line.textContent = state.car.speed > 2 ? Math.round(state.car.speed) + ' km/h' : 'Parked';
  else line.textContent = 'Parked · ' + metres(state.car.distance) + ' away';
}

function renderCard() {
  var label = $('card-label'), name = $('card-name'), line = $('card-line');
  var send = $('send'), clear = $('clear');

  if (selected) {
    label.textContent = 'Selected';
    name.textContent = selected.label;
    line.textContent = selected.meta || '';
    line.className = 'card-line';
    send.disabled = false;
    clear.disabled = false;
    return;
  }

  send.disabled = true;
  if (state.dest) {
    label.textContent = 'Destination';
    name.textContent = state.dest.label;
    line.textContent = driveLine();
    var st = state.drive ? state.drive.state : '';
    line.className = st === 'Driving' || st === 'Arrived' ? 'card-line live' : (st === 'Aborted' ? 'card-line warn' : 'card-line');
    clear.disabled = false;
    return;
  }

  label.textContent = 'Destination';
  name.textContent = 'Choose a place or tap the map';
  line.textContent = '';
  line.className = 'card-line';
  clear.disabled = true;
}

function renderList() {
  listEl.replaceChildren();
  var q = query.toLowerCase();
  var shown = 0;
  for (var i = 0; i < places.length; i++) {
    var p = places[i];
    if (q && p.label.toLowerCase().indexOf(q) < 0) continue;
    listEl.appendChild(row(p));
    shown++;
  }
  if (shown === 0) {
    var empty = document.createElement('div');
    empty.className = 'empty';
    empty.textContent = q ? 'No place matches.' : 'No places on the map yet.';
    listEl.appendChild(empty);
  }
}

function row(p) {
  var el = document.createElement('div');
  el.className = 'row' + (selected && selected.id === p.id ? ' on' : '');

  var sw = document.createElement('div');
  sw.className = 'swatch ' + p.kind;
  el.appendChild(sw);

  var text = document.createElement('div');
  text.className = 'text';
  var n = document.createElement('div');
  n.className = 'name';
  n.textContent = p.label;
  var m = document.createElement('div');
  m.className = 'meta';
  m.textContent = (KIND[p.kind] || 'Place') + ' · ' + metres(p.distance);
  text.appendChild(n);
  text.appendChild(m);
  el.appendChild(text);

  el.addEventListener('click', function () {
    selectPlace(p);
    centreOn(p.x, p.y);
  });
  return el;
}

// ---- actions ---------------------------------------------------------------------------------------------------

function selectPlace(p) {
  selected = { type: 'place', id: p.id, label: p.label, x: p.x, y: p.y, meta: (KIND[p.kind] || 'Place') + ' · ' + metres(p.distance) };
  flash('');
  renderAll();
}

function selectPoint(x, y) {
  selected = { type: 'point', id: '', label: 'Dropped pin', x: x, y: y, meta: '' };
  flash('');
  renderAll();
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
  if (selected) { selected = null; renderAll(); return; }
  if (s1.call('yesla.clear', '') !== 'ok') { flash('Could not clear the destination.'); return; }
  read();
  renderAll();
}

function renderAll() {
  renderMarks();
  renderHead();
  renderCard();
  renderList();
}

$('send').addEventListener('click', send);
$('clear').addEventListener('click', clear);

$('search').addEventListener('input', function (e) {
  query = (e.value !== undefined ? e.value : $('search').value) || '';
  renderList();
});

$('locate').addEventListener('click', function () {
  skipTap = true;
  var t = state.car || state.player;
  if (t) centreOn(t.x, t.y);
});

viewportEl.addEventListener('click', function (e) {
  if (skipTap) { skipTap = false; return; }
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

// Back first drops a selection that was not sent, then closes the app.
document.addEventListener('back', function (e) {
  if (!selected) return;
  e.preventDefault();
  selected = null;
  renderAll();
});

s1.on('yesla.changed', function () {
  read();
  renderMarks();
  renderHead();
  renderCard();
});

s1.on('yesla.places', function () {
  readPlaces();
  renderSpots();
  renderList();
});

read();
readPlaces();
renderSpots();
renderAll();
var start = state.car || state.player;
if (start) { zoom = ZOOM_START; centreOn(start.x, start.y); placed = true; } else apply();
