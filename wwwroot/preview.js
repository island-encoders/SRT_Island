const id = new URLSearchParams(location.search).get('id');
const feed = document.querySelector('#feed');
const state = document.querySelector('#feedStatus');
const heading = document.querySelector('#feedTitle');
let streaming = false;

async function update() {
  try {
    const rows = await (await fetch('/api/routes')).json();
    const item = rows.find(({ route }) => route.id === id);
    if (!item) { state.textContent = 'Slot is unavailable.'; return; }
    heading.textContent = `Slot ${item.route.slot} · ${item.route.name}`;
    state.textContent = item.status.state;
    if (item.status.hasPreview && !streaming) {
      streaming = true;
      feed.src = `/api/routes/${id}/live-preview?v=${Date.now()}`;
    }
    feed.hidden = !item.status.hasPreview;
    if (!item.status.active && streaming) { feed.removeAttribute('src'); streaming = false; }
  } catch { state.textContent = 'Waiting for SRT Island.'; }
  finally { setTimeout(update, 1000); }
}
feed.onerror = () => { streaming = false; };
update();
