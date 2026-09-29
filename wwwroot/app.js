let me, assets = [], selected;
const $ = id => document.getElementById(id);
const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
async function api(path, options = {}) {
  const response = await fetch(path, { credentials: 'same-origin', ...options, headers: { 'Content-Type': 'application/json', ...(options.method ? { 'X-CSRF-TOKEN': me.csrf } : {}), ...options.headers } });
  if (!response.ok) { let detail; try { detail = (await response.json()).detail; } catch {} throw new Error(detail || `HTTP ${response.status}`); }
  return response.status === 204 ? null : response.json();
}
function message(text, error = false) { $('message').textContent = text; $('message').className = error ? 'error' : ''; }
function view(name) {
  for (const item of ['assets','reports','admin']) $(item+'View').hidden = item !== name;
  document.querySelectorAll('nav button').forEach(b => b.classList.toggle('active', b.dataset.view === name));
  $('title').textContent = ({assets:'Moje zasoby',reports:'Moje zgłoszenia',admin:'Administracja'})[name];
  $('subtitle').textContent = ({assets:'Sprawdź przypisane elementy i zgłoś niezgodność w danych.',reports:'Śledź decyzje dotyczące Twoich zgłoszeń.',admin:'Oceń zgłoszenia przed zmianą danych źródłowych.'})[name];
  message(''); if (name === 'reports' || name === 'admin') loadReports();
}
async function loadAssets() {
  $('assets').innerHTML = '<p class="empty">Pobieranie zasobów…</p>';
  try {
    assets = await api('/api/assets');
    $('assets').innerHTML = assets.length ? assets.map((a, i) => `<article class="card"><div class="cardTop"><span class="iconBadge">▣</span><span class="key">${escapeHtml(a.key)}</span></div><h3>${escapeHtml(a.name)}</h3><dl class="details">${a.attributes.filter(x => x.value && x.name).slice(0,4).map(x => `<div><dt>${escapeHtml(x.name)}</dt><dd>${escapeHtml(x.value)}</dd></div>`).join('')}</dl><button data-asset="${i}">Zgłoś niezgodność</button></article>`).join('') : '<p class="empty">Brak przypisanych zasobów. Jeśli oczekujesz urządzenia, skontaktuj się z administratorem.</p>';
  } catch(e) { $('assets').innerHTML = `<p class="empty">Nie udało się pobrać zasobów. ${escapeHtml(e.message)}</p>`; }
}
function statusName(s) { return ({Pending:'Oczekuje',Approved:'Zatwierdzone',Rejected:'Odrzucone'})[s] || s; }
function reportRow(r, admin) {
  return `<article class="row"><div><h3>${escapeHtml(r.name)} <span class="key">${escapeHtml(r.key)}</span></h3><p><strong>${escapeHtml(r.attribute)}:</strong> ${escapeHtml(r.oldValue)} → ${escapeHtml(r.proposedValue)}</p><p>${escapeHtml(r.reason)}</p><p class="meta">${escapeHtml(r.reporter)} · ${escapeHtml(new Date(r.createdAt).toLocaleString('pl-PL'))} · ${escapeHtml(r.source)}</p></div>${admin && r.status === 'Pending' ? `<div class="decision"><button data-decision="approve" data-id="${r.id}">Zatwierdź</button><button class="reject" data-decision="reject" data-id="${r.id}">Odrzuć</button></div>` : `<span class="badge ${escapeHtml(r.status)}">${escapeHtml(statusName(r.status))}</span>`}</article>`;
}
async function loadReports() {
  try {
    const rows = await api('/api/reports');
    $('reports').innerHTML = rows.filter(r => r.reporter.toLowerCase() === me.name.toLowerCase()).map(r => reportRow(r,false)).join('') || '<p class="empty">Nie masz jeszcze zgłoszeń.</p>';
    if (me.admin) { $('adminReports').innerHTML = rows.map(r => reportRow(r,true)).join('') || '<p class="empty">Brak zgłoszeń.</p>'; const count = rows.filter(r => r.status === 'Pending').length; $('pendingCount').textContent = count || ''; }
  } catch(e) { message(e.message,true); }
}
document.querySelector('nav').addEventListener('click', e => { const b = e.target.closest('button[data-view]'); if (b) view(b.dataset.view); });
$('assets').addEventListener('click', e => {
  const b = e.target.closest('[data-asset]'); if (!b) return;
  selected = assets[Number(b.dataset.asset)]; $('dialogTitle').textContent = selected.name;
  $('attribute').innerHTML = selected.attributes.filter(x => x.id && me.allowedAttributes.includes(x.id)).map(x => `<option value="${escapeHtml(x.id)}">${escapeHtml(x.name)}</option>`).join('');
  if (!$('attribute').options.length) { message('Brak pól dopuszczonych do korekty dla tego zasobu.', true); return; }
  $('reportForm').reset(); $('currentValue').textContent = selected.attributes.find(x => x.id === $('attribute').value)?.value || '—';
  $('reportDialog').showModal();
});
$('attribute').addEventListener('change', () => { $('currentValue').textContent = selected.attributes.find(x => x.id === $('attribute').value)?.value || '—'; });
$('closeDialog').onclick = $('cancelDialog').onclick = () => $('reportDialog').close();
$('reportForm').addEventListener('submit', async e => {
  e.preventDefault(); try { await api('/api/reports',{method:'POST',body:JSON.stringify({objectId:selected.id,attributeId:$('attribute').value,proposedValue:$('proposed').value,reason:$('reason').value})}); $('reportDialog').close(); view('reports'); message('Zgłoszenie zostało zapisane.'); } catch(err) { message(err.message,true); $('reportDialog').close(); }
});
$('adminReports').addEventListener('click', async e => {
  const b = e.target.closest('[data-decision]'); if (!b) return;
  const action = b.dataset.decision; if (!confirm(action === 'approve' ? 'Zatwierdzić i zapisać korektę w Jira?' : 'Odrzucić zgłoszenie?')) return;
  b.disabled = true; try { await api(`/api/reports/${b.dataset.id}/decision`,{method:'POST',body:JSON.stringify({action})}); await loadReports(); message('Decyzja została zapisana.'); } catch(err) { message(err.message,true); b.disabled = false; }
});
$('refresh').onclick = loadAssets; $('refreshAdmin').onclick = loadReports;
(async () => { try { me = await api('/api/me'); $('identity').textContent = me.name; $('adminNav').hidden = !me.admin; await loadAssets(); } catch(e) { $('assets').innerHTML = '<p class="empty">Brak dostępu. Sprawdź Windows Authentication w IIS.</p>'; } })();
