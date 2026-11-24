const get = id => document.getElementById(id);
const editor = get('editor');
const form = get('routeForm');
const settings = get('settingsForm');
const cards = new Map();
const slotHosts = Array.from({length: 8}, (_, index) => {
  const host = document.createElement('div');
  const empty = document.createElement('article');
  empty.className = 'panel fields';
  const name = document.createElement('h2');
  name.textContent = `Slot ${index + 1}`;
  empty.append(name, button('Configure', () => openEditor({ slot: index + 1 })));
  host.append(empty);
  get('cards').append(host);
  return {host, empty};
});
const dashboardSlots = Array.from({length: 8}, (_, index) => {
  const item = document.createElement('article');
  item.className = 'dashSlot';
  const name = document.createElement('strong');
  const link = document.createElement('a');
  link.className = 'preview';
  link.target = '_blank';
  link.rel = 'noopener';
  link.setAttribute('aria-label', `Open live preview for slot ${index + 1}`);
  const label = document.createElement('span');
  const image = document.createElement('img');
  image.alt = 'Incoming video';
  image.hidden = true;
  link.append(label, image);
  item.append(name, link);
  get('dashboardSlots').append(item);
  return { name, link, label, image };
});
let routes = [];
let devices = [];
let displays = [];
let displayAudio = [];
let ajaAvailable = false;
let ajaDevices = [];
let editing = null;
let editingSlot = 0;
let logging = null;
let refreshVersion = 0;
let encoderEntries = [];
let peers = [];
let encoderId = null;
const emptyId = '00000000-0000-0000-0000-000000000000';

async function api(path, method = 'GET', body) {
  const response = await fetch('/api/' + path, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : {},
    body: body ? JSON.stringify(body) : undefined
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new Error(problem.error || `Request failed (${response.status})`);
  }
  return response.status === 204 ? undefined : response.json();
}

function notice(message = '') {
  get('notice').textContent = message;
  get('notice').hidden = !message;
}

async function action(run) {
  try { notice(); await run(); } catch (error) { notice(error.message); }
}

function navigate() {
  const pages = ['dashboard', 'routes', 'outputs', 'encoders', 'settings'];
  const page = pages.find(id => '#' + id === location.hash) || 'dashboard';
  for (const id of pages) get(id).hidden = id !== page;
  document.querySelectorAll('nav a').forEach(link => {
    if (link.hash === '#' + page) link.setAttribute('aria-current', 'page');
    else link.removeAttribute('aria-current');
  });
}

function isDisplay(device) { return device.startsWith('gpu|'); }
function isAja(device) { return device.startsWith('aja|'); }
function outputName(device) {
  if (isDisplay(device)) return displays.find(display => display.device === device)?.name || 'GPU display';
  if (isAja(device)) {
    const [, card, , connector] = device.split('|');
    return card ? `AJA ${card} · ${connector.toUpperCase()}` : 'AJA SDI / HDMI';
  }
  return device.split(' | ').at(-1);
}

function renderDevices() {
  const root = get('deckLinkCards');
  const expanded = new Set(Array.from(root.querySelectorAll('details[open]'), card => card.dataset.key));
  const groups = new Map();
  for (const device of devices) {
    const [id, name] = device.split(' | ');
    const [family, serial] = id.split(':');
    const ports = /\bQuad\b/i.test(name) ? 8 : /\bDuo\b/i.test(name) ? 4 : 1;
    const group = ports > 1 && /^[0-9a-f]+$/i.test(serial || '') ?
      family + ':' + (BigInt('0x' + serial) / BigInt(ports)).toString(16) : id;
    if (!groups.has(group)) groups.set(group, []);
    groups.get(group).push({ device, id, name });
  }
  root.replaceChildren(...Array.from(groups, ([key, ports]) => {
    const title = ports[0].name.replace(/\s*\(\d+\)$/, '');
    const panel = hardwareGroup(`${title} · ${ports.length} outputs`);
    panel.dataset.key = key;
    panel.open = expanded.has(key);
    for (const port of ports) {
      const assigned = routes.filter(({ route }) => route.device === port.device).map(({ route }) => `Slot ${route.slot}: ${route.name}`);
      panel.append(deviceRow(port.name, port.id, assigned.join(', ') || 'Unassigned', [
        button('Set up test pattern', () => openEditor({ name: 'Test · ' + port.name, device: port.device, input: 'pattern' }))
      ]));
    }
    return panel;
  }));
  const information = document.createElement('p');
  information.textContent = devices.length ? '' : 'No DeckLink playback outputs detected.';
  root.append(information);
}

function hardwareGroup(title) {
  const panel = document.createElement('details');
  panel.className = 'panel fleet hardware';
  const summary = document.createElement('summary');
  summary.textContent = title;
  panel.append(summary);
  return panel;
}

async function scan() {
  const [cardsResult, displaysResult, audioResult, gpuResult, ajaResult] = await Promise.allSettled(
    [api('devices'), api('displays'), api('display-audio'), api('gpus'), api('aja-devices')]);
  if (cardsResult.status === 'fulfilled') {
    devices = cardsResult.value;
  } else devices = [];
  displays = displaysResult.status === 'fulfilled' ? displaysResult.value : [];
  displayAudio = audioResult.status === 'fulfilled' ? audioResult.value : [];
  get('displayInfo').textContent = displaysResult.status === 'rejected' ? displaysResult.reason.message :
    displays.some(display => display.available) ? '' : 'Connect a second display and select Extend these displays in Windows.';
  renderDisplays();
  renderDevices();
  ajaDevices = ajaResult.value || [];
  const ajaGroups = new Map();
  for (const target of new Set([...ajaDevices, ...routes.map(({route}) => route.device).filter(isAja)])) {
    const [, card] = target.split('|');
    if (!ajaGroups.has(card)) ajaGroups.set(card, []);
    ajaGroups.get(card).push(target);
  }
  get('ajaCards').replaceChildren(...Array.from(ajaGroups, ([card, ports]) => {
    const panel = hardwareGroup(`AJA ${card}`);
    for (const target of ports) {
      const [,,, connector] = target.split('|');
      panel.append(deviceRow(connector.toUpperCase(), `Video channel ${target.split('|')[2]}`,
        routes.filter(({route}) => route.device === target).map(({route}) => `Slot ${route.slot}`).join(', ') || 'Unassigned', [
          button('Set up test pattern', () => openEditor({device: target, input: 'pattern', name: 'Test · ' + outputName(target)}))
        ]));
    }
    return panel;
  }));
  get('gpuInventory').replaceChildren(...(gpuResult.value || []).map(gpu => {
    const panel = hardwareGroup(gpu.name);
    for (const output of gpu.outputs)
      panel.append(deviceRow(output.connector || 'Windows display target', output.name || `Target ${output.id}`, output.state, []));
    return panel;
  }));
}

function renderDisplays() {
  get('displays').replaceChildren(...displays.map(display => {
    const roles = display.primary ? 'Desktop · main display' : display.mirrored ? 'Mirrored · extend to use for playout' :
      display.available ? 'Available for dedicated playout' : 'Display topology unavailable';
    const identify = button('Identify', () => action(() => api('displays/identify', 'POST', { device: display.device })));
    const outputs = [identify];
    if (display.available)
      outputs.push(button('Set up test pattern', () => openEditor({ name: 'Test · ' + display.name, device: display.device, input: 'pattern' })));
    return deviceRow(display.name, [display.windowsName.split('\\').at(-1), display.connector, `${display.width} × ${display.height}`].filter(Boolean).join(' · '), roles, outputs);
  }));
}

async function refresh() {
  const version = ++refreshVersion;
  const results = await api('routes');
  if (version !== refreshVersion) return;
  routes = results;
  get('total').textContent = `${routes.filter(({route}) => route.slot > 0).length} / 8`;
  get('live').textContent = routes.filter(({ status }) => status.hasPreview).length;
  get('assigned').textContent = routes.filter(({ status }) => status.playingOut).length;
  for (const [index, entry] of dashboardSlots.entries()) {
    const item = routes.find(({ route }) => route.slot === index + 1);
    entry.name.textContent = `Slot ${index + 1}${item ? ' · ' + item.route.name : ''}`;
    entry.label.textContent = item ? item.status.state : 'Unconfigured';
    slotHosts[index].empty.hidden = Boolean(item);
    setPreview(entry.link, entry.image, item?.route.id, item?.status.hasPreview && !get('dashboard').hidden && !document.hidden);
  }
  const ids = new Set(routes.map(({ route }) => route.id));
  for (const [id, card] of cards) if (!ids.has(id)) { card.remove(); cards.delete(id); }
  for (const { route, status } of routes) {
    let card = cards.get(route.id);
    if (!card) {
      card = get('card').content.firstElementChild.cloneNode(true);
      (slotHosts[route.slot - 1]?.host || get('cards')).append(card);
      cards.set(route.id, card);
    }
    const select = selector => card.querySelector(selector);
    select('h2').textContent = route.slot ? `Slot ${route.slot} · ${route.name}` : route.name;
    select('.state').textContent = status.state;
    select('.state').dataset.state = status.state;
    select('.input').textContent = { file: route.file, pattern: 'Colour bars + 1 kHz', srt: `${route.mode} · ${route.host}:${route.port}` }[route.input];
    const format = form.elements.rate.querySelector(`option[value="${route.rate}"]`)?.textContent;
    const outputFormat = isDisplay(route.device) ? 'GPU display' : format;
    select('.output').textContent = route.device ? `${outputName(route.device)} · ${outputFormat}${status.playingOut ? ' · Active' : ''}` : 'Preview only';
    select('.video').textContent = status.video || '—';
    select('.audio').textContent = status.audio || (route.input === 'pattern' ? '1 kHz tone' : '—');
    const restream = select('.restreamLinks');
    const destinations = route.destinations || [];
    const restreamKey = JSON.stringify([status.active, destinations.map(target => [target.name, target.protocol])]);
    if (restream.dataset.key !== restreamKey) {
      restream.dataset.key = restreamKey;
      const summary = document.createElement('small');
      summary.textContent = destinations.length ? `${destinations.length} restream destination${destinations.length === 1 ? '' : 's'}` : '';
      restream.replaceChildren(...(destinations.length ? [summary] : []));
      if (status.active) destinations.forEach((target, index) => {
        if (target.protocol === 'hls') restream.append(button(`Copy HLS URL · ${target.name}`, () => action(() =>
          navigator.clipboard.writeText(new URL(`/hls/${route.id}/${index + 1}/index.m3u8`, location.origin).href))));
      });
    }
    setPreview(select('.preview'), select('img'), route.id, status.hasPreview && !get('routes').hidden && !document.hidden);
    const toggle = select('.toggle');
    toggle.textContent = status.active ? 'Stop' : 'Start';
    toggle.disabled = status.state === 'Stopping';
    toggle.onclick = () => action(async () => {
      await api(`routes/${route.id}/${status.active ? 'stop' : 'start'}`, 'POST');
      await refresh();
    });
    select('.edit').disabled = select('.delete').disabled = status.active;
    select('.edit').onclick = () => openEditor(route);
    select('.delete').textContent = 'Clear slot';
    select('.delete').onclick = () => action(async () => {
      if (!window.confirm(`Clear the configuration for “${route.name}”?`)) return;
      await api('routes/' + route.id, 'DELETE');
      await refresh();
    });
    select('.log').onclick = () => {
      logging = route.id;
      get('log').textContent = status.log.join('\n') || 'No log messages yet.';
      get('details').showModal();
    };
    if (logging === route.id && get('details').open)
      get('log').textContent = status.log.join('\n') || 'No log messages yet.';
  }
}

function setPreview(link, image, id, active) {
  if (active) {
    link.href = `/preview.html?id=${id}`;
    image.onload = () => { image.hidden = false; };
    image.src = `/api/routes/${id}/preview?v=${Date.now()}`;
  } else {
    link.removeAttribute('href');
    image.onload = null;
    image.hidden = true;
  }
}

function updateFields() {
  const input = form.elements.input.value;
  get('restreamEditor').hidden = input !== 'srt';
  for (const field of get('destinationRows').querySelectorAll('[data-key]')) field.disabled = input !== 'srt';
  for (const field of form.querySelectorAll('[data-input]')) field.hidden = field.dataset.input !== input;
  get('recordOption').hidden = input === 'pattern';
  const gpu = isDisplay(form.elements.device.value);
  const aja = isAja(form.elements.device.value);
  get('audioPairs').hidden = input === 'pattern' || !form.elements.device.value || gpu;
  get('hardwareRate').hidden = gpu;
  get('displayAudio').hidden = !gpu;
  get('exclusiveAudio').hidden = !gpu || form.elements.displayAudio.value === 'mute';
  get('ajaFields').hidden = !aja;
  for (const field of ['ajaCard', 'ajaChannel', 'ajaConnector']) form.elements[field].disabled = !aja;
  const bonded = form.elements.mode.value === 'bonded';
  form.elements.host.title = bonded ? 'Enter one wildcard or comma-separated bind addresses' : '';
  get('bondSecretField').hidden = !bonded;
  get('streamIdField').hidden = bonded;
}

async function openEditor(route = {}) {
  editingSlot = route.slot ?? Array.from({length: 8}, (_, index) => index + 1)
    .find(slot => !routes.some(item => item.route.slot === slot));
  if (editingSlot === undefined) { notice('All eight slots are configured. Edit or clear an existing slot.'); return; }
  editing = route.id || null;
  form.reset();
  get('destinationRows').replaceChildren();
  for (const target of route.destinations || []) addDestination(target);
  get('restreamEditor').open = Boolean(route.destinations?.length);
  if (!route.port && editingSlot > 0) form.elements.port.value = 8999 + editingSlot;
  get('formError').hidden = true;
  get('editorTitle').textContent = editingSlot ? `Configure slot ${editingSlot}` : 'Configure route';
  for (const [key, value] of Object.entries(route)) {
    const field = form.elements.namedItem(key);
    if (!field || key === 'device' || key === 'displayAudio') continue;
    if (field.type === 'checkbox') field.checked = value;
    else field.value = value;
  }
  const deviceField = form.elements.device;
  const audioField = form.elements.displayAudio;
  let selectedDevice = route.device || '';
  if (isAja(selectedDevice)) {
    const [, card, channel, connector] = selectedDevice.split('|');
    if (card) {
      form.elements.ajaCard.value = card;
      form.elements.ajaChannel.value = channel;
      form.elements.ajaConnector.value = connector;
    }
    selectedDevice = 'aja|';
  }
  const fill = (current, endpoint) => {
    const gpu = displays.filter(display => display.available).map(display => display.device);
    const choices = new Set([...devices, ...gpu, ajaAvailable ? 'aja|' : '', current].filter(Boolean));
    deviceField.replaceChildren(new Option('Preview only', ''), ...Array.from(choices, value =>
      new Option((isDisplay(value) ? 'GPU · ' : isAja(value) ? '' : 'DeckLink · ') + outputName(value), value)));
    deviceField.value = current;
    audioField.replaceChildren(new Option('Mute', 'mute'), ...displayAudio.map(audio => new Option(audio.name, audio.id)));
    if (endpoint !== 'mute' && !displayAudio.some(audio => audio.id === endpoint))
      audioField.add(new Option('Saved endpoint (unavailable)', endpoint));
    audioField.value = endpoint;
    updateFields();
  };
  fill(selectedDevice, route.displayAudio || 'mute');
  if (!route.rate) form.elements.rate.value = '50';
  updateFields();
  editor.showModal();
  if (!devices.length || !displays.length) {
    await scan();
    if (editor.open) fill(deviceField.value, audioField.value);
  }
}

function addDestination(target = {}) {
  const root = get('destinationRows');
  const row = get('destinationTemplate').content.firstElementChild.cloneNode(true);
  const fields = Object.fromEntries(Array.from(row.querySelectorAll('[data-key]'), field => [field.dataset.key, field]));
  for (const [key, value] of Object.entries(target)) if (fields[key]) fields[key].value = value;
  const update = () => {
    const local = fields.protocol.value === 'hls';
    row.querySelector('.destinationUrl').hidden = local;
    fields.url.required = !local && form.elements.input.value === 'srt';
    row.querySelector('.encode').hidden = fields.mode.value !== 'encode';
  };
  fields.protocol.onchange = fields.mode.onchange = update;
  row.querySelector('.removeDestination').onclick = () => row.remove();
  root.append(row);
  update();
}
get('addDestination').onclick = () => addDestination();

form.elements.input.onchange = updateFields;
form.elements.device.onchange = updateFields;
form.elements.displayAudio.onchange = updateFields;
form.elements.mode.onchange = () => {
  const host = form.elements.host;
  const listening = ['listener', 'bonded'].includes(form.elements.mode.value);
  if (!listening && host.value === '0.0.0.0') host.value = '';
  if (listening && !host.value) host.value = '0.0.0.0';
  updateFields();
};
form.onsubmit = async event => {
  event.preventDefault();
  const value = Object.fromEntries(new FormData(form));
  value.destinations = value.input !== 'srt' ? [] : Array.from(get('destinationRows').children, row =>
    Object.fromEntries(Array.from(row.querySelectorAll('[data-key]'), field =>
      [field.dataset.key, field.type === 'number' ? Number(field.value) : field.value])));
  value.slot = editingSlot;
  value.id = editing || emptyId;
  for (const key of ['port', 'latency', 'audioTrack', 'audioPairs']) value[key] = Number(value[key]);
  if (value.mode !== 'bonded') value.bondPassphrase = '';
  value.record = form.elements.record.checked && value.input !== 'pattern';
  value.reconnect = form.elements.reconnect.checked;
  if (!isDisplay(value.device)) value.displayAudio = 'mute';
  value.exclusiveDisplayAudio = value.displayAudio !== 'mute' && form.elements.exclusiveDisplayAudio.checked;
  if (isAja(value.device)) value.device = `aja|${value.ajaCard}|${value.ajaChannel}|${value.ajaConnector}`;
  for (const key of ['ajaCard', 'ajaChannel', 'ajaConnector']) delete value[key];
  try {
    await api('routes', 'POST', value);
    editor.close();
    await refresh();
  } catch (error) {
    get('formError').textContent = error.message;
    get('formError').hidden = false;
  }
};

settings.onsubmit = event => {
  event.preventDefault();
  action(async () => {
    const config = Object.fromEntries(new FormData(settings));
    await api('settings', 'PUT', config);
    setAjaAvailability(config.ajaPlayer);
    await scan();
  });
};

function setAjaAvailability(path) {
  ajaAvailable = Boolean(path);
  get('ajaTest').disabled = !ajaAvailable;
  get('ajaInfo').textContent = ajaAvailable ? 'Choose the card, video channel and connector in the route editor.' :
    'Set the AJA GStreamer launcher in Settings to configure an output.';
}
get('scan').onclick = scan;
get('windowsDisplay').onclick = () => action(() => api('displays/settings', 'POST'));
get('ajaTest').onclick = () => openEditor({ name: 'Test · AJA', device: 'aja|', input: 'pattern' });
document.querySelectorAll('[data-close]').forEach(button => button.onclick = () => button.closest('dialog').close());
window.addEventListener('hashchange', () => {
  navigate();
  if (!get('outputs').hidden) scan();
  if (!get('encoders').hidden) action(loadFleet);
});
navigate();
action(async () => {
  const config = await api('settings');
  const remote = await api('remote-control');
  get('remoteControlStatus').textContent = remote.enabled ? `Enabled on local port ${remote.port}.` : 'Disabled for this session.';
  for (const [key, value] of Object.entries(config)) settings.elements.namedItem(key).value = value;
  setAjaAvailability(config.ajaPlayer);
  await refresh();
  if (!get('outputs').hidden) await scan();
  if (!get('encoders').hidden) await loadFleet();
});
async function poll() {
  try { await refresh(); }
  catch (error) { notice(error.message); }
  finally { setTimeout(poll, 900); }
}
setTimeout(poll, 900);

async function refreshDashboard() {
  if (get('dashboard').hidden) return;
  try {
    const status = await api('dashboard');
    const minutes = Math.floor(status.uptimeSeconds / 60);
    get('uptime').textContent = `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
    get('memory').textContent = `${Math.round(status.memoryBytes / 1048576)} MB`;
    get('addresses').textContent = status.addresses.join(' · ') || '—';
    get('encoderCount').textContent = status.savedEncoders;
  } catch { /* Keep the last station reading while the app reconnects. */ }
}
refreshDashboard();
setInterval(refreshDashboard, 4000);

function button(label, click) {
  const element = document.createElement('button');
  element.type = 'button';
  element.textContent = label;
  element.onclick = click;
  return element;
}

function deviceRow(name, detail, presence, buttons) {
  const item = document.createElement('div');
  item.className = 'fleetRow';
  const text = document.createElement('div');
  const title = document.createElement('strong');
  title.textContent = name;
  const addresses = document.createElement('p');
  addresses.textContent = detail;
  const state = document.createElement('span');
  state.className = 'presence' + (presence === 'Online' ? ' online' : '');
  state.textContent = presence;
  text.append(title, addresses, state);
  const actions = document.createElement('div');
  actions.className = 'rowActions';
  actions.append(...buttons);
  item.append(text, actions);
  return item;
}

function addressMatches(address, peer) {
  if (!address) return false;
  const host = new URL(address).hostname.toLowerCase().replace(/^\[|\]$/g, '').replace(/\.$/, '');
  return [peer.dns, peer.name, ...peer.ips].some(value => value.toLowerCase() === host);
}

function peerState(peer) {
  if (peer.online) return 'Online';
  const seen = peer.lastSeen ? new Date(peer.lastSeen) : null;
  return seen && seen.getUTCFullYear() > 2000 ? 'Offline · last seen ' + seen.toLocaleString() : 'Offline';
}

function renderBook() {
  get('encoderBook').replaceChildren(...encoderEntries.map(entry => {
    const peer = peers.find(node => addressMatches(entry.tailnetAddress, node) || addressMatches(entry.address, node));
    const actions = [button('Connect', () => connectEntry(entry, false))];
    if (entry.tailnetAddress && entry.tailnetAddress !== entry.address)
      actions.push(button('Connect via Tailscale', () => connectEntry(entry, true)));
    actions.push(button('Edit', () => editEncoder(entry)), button('Remove', () => action(async () => {
      if (!window.confirm(`Remove “${entry.name}” from your address book?`)) return;
      await api('encoders/' + entry.id, 'DELETE');
      await loadBook();
    })));
    return deviceRow(entry.name, [entry.address, entry.tailnetAddress].filter(Boolean).join(' · '),
      peer ? peerState(peer) : 'Tailnet status unavailable', actions);
  }));
  if (!encoderEntries.length) get('encoderBook').textContent = 'Your address book is empty.';
}

async function loadBook() { encoderEntries = await api('encoders'); renderBook(); }

async function loadPeers() {
  const filters = get('tailnetForm').elements;
  const tag = filters.tag.value.trim();
  try {
    const result = await api((filters.source.value === 'encoder' ? 'encoder/' : '') + 'tailnet?tag=' + encodeURIComponent(tag));
    peers = result.peers;
    get('tailnetInfo').textContent = result.state !== 'Running' ? 'Windows Tailscale is ' + result.state.toLowerCase() + '.' :
      peers.length ? '' : 'No matching peers are visible from this source.';
  } catch (error) {
    peers = [];
    get('tailnetInfo').textContent = error.message;
  }
  get('peers').replaceChildren(...peers.map(peer => {
    const ip = peer.ips.find(value => !value.includes(':'));
    const url = ip ? 'http://' + ip : peer.dns ? 'https://' + peer.dns : '';
    const listed = encoderEntries.some(entry => addressMatches(entry.address, peer) || addressMatches(entry.tailnetAddress, peer));
    return deviceRow(peer.name, [ip, peer.dns].filter(Boolean).join(' · '), peerState(peer),
      url && !listed ? [button('Add to address book', () => editEncoder({ name: peer.name, address: url, tailnetAddress: url }))] : []);
  }));
  renderBook();
}

async function loadFleet() { await loadBook(); await loadPeers(); }

function editEncoder(entry = {}) {
  const entryForm = get('encoderForm');
  encoderId = entry.id || emptyId;
  for (const key of ['name', 'address', 'tailnetAddress', 'username']) entryForm.elements[key].value = entry[key] || '';
  entryForm.elements.password.value = '';
  entryForm.elements.password.placeholder = entry.hasPassword ? 'Leave blank to keep saved password' : 'Optional';
  entryForm.elements.forgetPassword.checked = false;
  get('forgetEncoderPassword').hidden = !entry.hasPassword;
  get('encoderEditor').showModal();
}

function showEncoderLogin(url, username = '') {
  const login = get('loginForm');
  login.reset();
  login.elements.address.value = url;
  login.elements.username.value = username;
  get('loginError').hidden = true;
  get('encoderLogin').showModal();
}

function connectEntry(entry, tailnet) {
  if (!entry.hasPassword) {
    showEncoderLogin(tailnet ? entry.tailnetAddress : entry.address, entry.username);
    return;
  }
  action(async () => {
    encoderControls(await api(`encoders/${entry.id}/connect`, 'POST', { tailnet }));
    get('encoderControl').scrollIntoView({ block: 'start' });
  });
}

function encoderControls(result) {
  get('encoderControl').hidden = false;
  get('connectedAddress').textContent = result.address;
  get('channels').replaceChildren(...result.channels.map(channel => {
    const card = get('channelForm').content.firstElementChild.cloneNode(true);
    card.querySelector('h2').textContent = channel.name;
    card.querySelector('.signal').textContent = channel.signal ? `${channel.width} × ${channel.height} · ${channel.fps} fps · ${channel.audioHz || '—'} Hz` : 'No input signal';
    card.querySelector('.encoding').textContent = [channel.codec, channel.audioCodec].filter(Boolean).join(' · ');
    card.elements.enabled.checked = channel.srt.enabled;
    for (const key of ['mode', 'host', 'port', 'latency']) card.elements[key].value = channel.srt[key];
    card.onsubmit = event => {
      event.preventDefault();
      action(async () => {
        if (!window.confirm(`Apply SRT output settings for ${channel.name} on ${result.address}?`)) return;
        const data = Object.fromEntries(new FormData(card));
        data.enabled = card.elements.enabled.checked;
        data.port = Number(data.port);
        data.latency = Number(data.latency);
        encoderControls(await api('encoder/channels/' + channel.id, 'PUT', data));
      });
    };
    card.querySelector('.makeRoute').onclick = () => {
      const mode = card.elements.mode.value;
      openEditor({
        name: channel.name + ' · ' + new URL(result.address).hostname,
        mode: mode === 'caller' ? 'listener' : mode === 'listener' ? 'caller' : mode,
        host: mode === 'caller' ? '0.0.0.0' : new URL(result.address).hostname,
        port: card.elements.port.value, latency: card.elements.latency.value
      });
    };
    return card;
  }));
}

get('newEncoder').onclick = () => editEncoder();
get('encoderForm').onsubmit = event => {
  event.preventDefault();
  action(async () => {
    const data = Object.fromEntries(new FormData(event.target));
    data.forgetPassword = event.target.elements.forgetPassword.checked;
    await api('encoders', 'POST', { id: encoderId, ...data });
    event.target.elements.password.value = '';
    get('encoderEditor').close();
    await loadBook();
  });
};
get('tailnetForm').onsubmit = event => { event.preventDefault(); loadPeers(); };
get('tailnetForm').elements.source.onchange = loadPeers;
get('refreshEncoder').onclick = () => action(async () => encoderControls(await api('encoder')));
get('disconnectEncoder').onclick = () => action(async () => {
  await api('encoder/disconnect', 'POST');
  get('encoderControl').hidden = true;
  get('channels').replaceChildren();
});
get('loginForm').onsubmit = async event => {
  event.preventDefault();
  const login = event.target;
  try {
    encoderControls(await api('encoder/connect', 'POST', Object.fromEntries(new FormData(login))));
    get('encoderLogin').close();
    get('encoderControl').scrollIntoView({ block: 'start' });
  } catch (error) {
    get('loginError').textContent = error.message;
    get('loginError').hidden = false;
  } finally { login.elements.password.value = ''; }
};
setInterval(() => { if (!document.hidden && !get('encoders').hidden) loadPeers(); }, 15000);
