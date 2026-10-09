let me, assets = [], selected;
const appBase = new URL('.', document.querySelector('script[src$="app.js"]').src);
const $ = id => document.getElementById(id);
const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
async function api(path, options = {}) {
  const url = new URL(path.replace(/^\/+/, ''), appBase);
  const response = await fetch(url, { credentials: 'same-origin', ...options, headers: { 'Content-Type': 'application/json', ...(options.method ? { 'X-CSRF-TOKEN': me.csrf } : {}), ...options.headers } });
  if (!response.ok) { let detail; try { const data = await response.json(); detail = data.detail || data.error; } catch {} throw new Error(detail || `HTTP ${response.status}`); }
  return response.status === 204 ? null : response.json();
}
function message(text, error = false) { $('message').textContent = text; $('message').className = error ? 'error' : ''; }
function view(name) {
  if ((name === 'admin' || name === 'settings' || name === 'entra') && !me?.admin) return;
  for (const item of ['assets','team','handover','permissions','teamPermissions','reports','admin','settings','entra']) $(item+'View').hidden = item !== name;
  document.querySelectorAll('nav button[data-view]').forEach(b => b.classList.toggle('active', b.dataset.view === name));
  const equipmentView = name === 'assets' || name === 'team' || name === 'handover';
  $('equipmentNav').classList.toggle('active', equipmentView);
  if (equipmentView) { $('equipmentSubnav').hidden = false; $('equipmentNav').setAttribute('aria-expanded', 'true'); }
  const permissionsView = name === 'permissions' || name === 'teamPermissions';
  $('permissionsNav').classList.toggle('active', permissionsView);
  if (permissionsView) { $('permissionsSubnav').hidden = false; $('permissionsNav').setAttribute('aria-expanded','true'); }
  const settingsView = name === 'settings' || name === 'entra';
  $('settingsNav').classList.toggle('active', settingsView);
  if (settingsView) { $('settingsSubnav').hidden = false; $('settingsNav').setAttribute('aria-expanded','true'); }
  $('title').textContent = ({assets:'M\u00f3j sprz\u0119t',team:'Sprz\u0119t mojego zespo\u0142u',handover:'Zdanie sprz\u0119tu',permissions:'Moje uprawnienia',teamPermissions:'Uprawnienia mojego zespo\u0142u',reports:'Moje zgłoszenia',admin:'Administracja',settings:'Ustawienia - Jira',entra:'Ustawienia - Entra ID'})[name];
  $('subtitle').textContent = ({assets:'Sprawdz przypisane elementy. Zglos sprzet, ktorego nie posiadasz.',team:'Zasoby bezposrednich i posrednich podwladnych.',handover:'Protok\u00f3\u0142 zdawczo-odbiorczy i zwrot sprz\u0119tu.',teamPermissions:'Dostepy bezposrednich i posrednich podwladnych.',permissions:'Przypisane dostepy do systemow i aplikacji.',reports:'Śledź decyzje dotyczące Twoich zgłoszeń.',admin:'Oceń zgłoszenia przed zmianą danych źródłowych.',entra:'Polaczenie Microsoft Entra ID i dane organizacji.',settings:'Połączenie Jira, uprawnienia administratorów i zakres korekt.'})[name];
  message(''); if (name === 'team') loadTeam(); if (name === 'reports' || name === 'admin') loadReports(); if (name === 'settings') loadSettings();
}
async function loadSettings() {
  try {
    const settings = await api('/api/admin/settings');
    for (const id of ['siteUrl','accountEmail','cloudId','workspaceId','aql','ownerAttributeId','adminGroup']) $(id).value = settings[id] || '';
    $('allowedIds').value = settings.allowedCorrectionAttributeIds.join(', ');
    $('apiToken').value = '';
    $('tokenStatus').textContent = settings.tokenConfigured ? 'Token zapisany. Pozostaw puste, aby go zachować.' : 'Token nie jest zapisany.';
  } catch(e) { message(e.message, true); }
}
async function loadAssets() {
  $('assets').innerHTML = '<p class="empty">Pobieranie zasobów…</p>';
  try {
    assets = await api('/api/assets');
    renderPersonalEquipment();
  } catch(e) { $('assets').innerHTML = `<p class="empty">Nie udało się pobrać zasobów. ${escapeHtml(e.message)}</p>`; }
}
function statusName(s) { return ({Pending:'Oczekuje',Approved:'Zatwierdzone',Rejected:'Odrzucone'})[s] || s; }
function reportRow(r, admin) {
  return `<article class="row"><div><h3>${escapeHtml(r.name)} <span class="key">${escapeHtml(r.key)}</span></h3><p><strong>${escapeHtml(({ 'not-held':'Nie posiadam tego sprzetu', 'employee-left':'Pracownik juz nie pracuje', assignment:'Nieprawidlowe przypisanie sprzetu' })[r.type] || r.attribute)}:</strong> ${escapeHtml(r.oldValue)} → ${escapeHtml(r.proposedValue)}</p><p>${escapeHtml(r.reason)}</p><p class="meta">${escapeHtml(r.reporter)}${r.affectedEmployee ? ' · Pracownik: ' + escapeHtml(r.affectedEmployee) : ''} · ${escapeHtml(new Date(r.createdAt).toLocaleString('pl-PL'))} · ${escapeHtml(r.source)}</p></div>${admin && r.status === 'Pending' ? `<div class="decision"><button data-decision="approve" data-id="${r.id}">Zatwierdź</button><button class="reject" data-decision="reject" data-id="${r.id}">Odrzuć</button></div>` : `<span class="badge ${escapeHtml(r.status)}">${escapeHtml(statusName(r.status))}</span>`}</article>`;
}
async function loadReports() {
  try {
    const rows = await api('/api/reports');
    $('reports').innerHTML = rows.filter(r => r.reporter.toLowerCase() === me.name.toLowerCase()).map(r => reportRow(r,false)).join('') || '<p class="empty">Nie masz jeszcze zgłoszeń.</p>';
    if (me.admin) { $('adminReports').innerHTML = rows.map(r => reportRow(r,true)).join('') || '<p class="empty">Brak zgłoszeń.</p>'; const count = rows.filter(r => r.status === 'Pending').length; $('pendingCount').textContent = String(count); }
  } catch(e) { message(e.message,true); }
}
$('permissionsNav').addEventListener('click', () => {
  const submenu = $('permissionsSubnav');
  submenu.hidden = !submenu.hidden;
  $('permissionsNav').setAttribute('aria-expanded', String(!submenu.hidden));
});
$('settingsNav').addEventListener('click', () => {
  const submenu = $('settingsSubnav');
  submenu.hidden = !submenu.hidden;
  $('settingsNav').setAttribute('aria-expanded', String(!submenu.hidden));
});
$('equipmentNav').addEventListener('click', () => {
  const submenu = $('equipmentSubnav');
  submenu.hidden = !submenu.hidden;
  $('equipmentNav').setAttribute('aria-expanded', String(!submenu.hidden));
});
document.querySelector('nav').addEventListener('click', e => { const b = e.target.closest('button[data-view]'); if (b) view(b.dataset.view); });
function openEquipmentReport(asset, owner = '') {
  selected = asset;
  $('reportForm').reset();
  $('dialogTitle').textContent = asset.name;
  $('dialogOwner').textContent = owner ? `Przypisany pracownik: ${owner}` : '';
  for (const option of $('reportType').options) {
    option.hidden = ['employee-left','assignment'].includes(option.value) && !owner;
    if (option.value === 'not-held') option.hidden = !!owner;
  }
  $('reportType').value = owner ? 'assignment' : 'not-held';
  $('attribute').innerHTML = asset.attributes.filter(x => x.id && me.allowedAttributes.includes(x.id)).map(x => `<option value="${escapeHtml(x.id)}">${escapeHtml(x.name)}</option>`).join('');
  $('attributeFields').hidden = true;
  $('attribute').required = $('proposed').required = false;
  $('currentValue').textContent = asset.attributes.find(x => x.id === $('attribute').value)?.value || '-';
  $('reportDialog').showModal();
}
$('assets').addEventListener('click', e => {
  const b = e.target.closest('[data-asset]');
  if (b) openEquipmentReport(assets[Number(b.dataset.asset)]);
});
$('attribute').addEventListener('change', () => { $('currentValue').textContent = selected.attributes.find(x => x.id === $('attribute').value)?.value || '—'; });
$('closeDialog').onclick = $('cancelDialog').onclick = () => $('reportDialog').close();
$('reportForm').addEventListener('submit', async e => {
  e.preventDefault(); try { await api('/api/reports',{method:'POST',body:JSON.stringify({objectId:selected.id,type:$('reportType').value,attributeId:$('reportType').value === 'attribute' ? $('attribute').value : null,proposedValue:$('reportType').value === 'attribute' ? $('proposed').value : '',reason:$('reason').value})}); $('reportDialog').close(); view('reports'); message('Zgłoszenie zostało zapisane.'); } catch(err) { message(err.message,true); $('reportDialog').close(); }
});
$('adminReports').addEventListener('click', async e => {
  const b = e.target.closest('[data-decision]'); if (!b) return;
  const action = b.dataset.decision; if (!confirm(action === 'approve' ? 'Zatwierdzic zgloszenie w podgladzie demo?' : 'Odrzucić zgłoszenie?')) return;
  b.disabled = true; try { await api(`/api/reports/${b.dataset.id}/decision`,{method:'POST',body:JSON.stringify({action})}); await loadReports(); await loadAssets(); loadTeam(); message('Decyzja została zapisana.'); } catch(err) { message(err.message,true); b.disabled = false; }
});
$('refresh').onclick = loadAssets; $('refreshAdmin').onclick = loadReports;
$('settingsForm').addEventListener('submit', async e => {
  e.preventDefault();
  const data = Object.fromEntries(['siteUrl','accountEmail','cloudId','workspaceId','aql','ownerAttributeId','adminGroup'].map(id => [id,$(id).value.trim()]));
  data.allowedCorrectionAttributeIds = $('allowedIds').value.split(',').map(x => x.trim()).filter(Boolean);
  data.apiToken = $('apiToken').value;
  try {
    await api('/api/admin/settings',{method:'POST',body:JSON.stringify(data)});
    me = await api('/api/me'); $('apiToken').value = ''; $('tokenStatus').textContent = 'Token zapisany. Pozostaw puste, aby go zachować.';
    message('Ustawienia zapisane.');
  } catch(err) { message(err.message,true); }
});
$('discoverWorkspace').onclick = async () => {
  try {
    const data = await api('/api/admin/jira/workspaces');
    const values = Array.isArray(data) ? data : data.values || data.workspaces || [];
    const ids = values.map(x => x.workspaceId || x.id).filter(Boolean);
    if (ids.length === 1) { $('workspaceId').value = ids[0]; message('Wykryto workspace. Zapisz ustawienia.'); }
    else message(ids.length ? `Dostępne workspace ID: ${ids.join(', ')}` : 'Jira nie zwróciła workspace ID.', !ids.length);
  } catch(err) { message(err.message,true); }
};
setupEquipmentFilters('asset','assetFilters',renderPersonalEquipment);
(async () => {
  try {
    me = await api('/api/me');
    $('identity').textContent = me.name || 'Nieznane konto';
    $('identityRole').textContent = me.admin ? 'Administrator' : 'Użytkownik';
    $('accountBadge').textContent = `${me.name} · ${me.admin ? 'Administrator' : 'Użytkownik'}`;
    $('adminNav').hidden = $('settingsGroup').hidden = !me.admin;
    await Promise.all([loadAssets(), loadReports()]);
    view('team');
  } catch(e) {
    $('identity').textContent = 'Brak tożsamości Windows';
    $('identityRole').textContent = 'Nie uwierzytelniono';
    $('accountBadge').textContent = 'Nie zalogowano';
    $('assets').innerHTML = `<p class="empty">Aplikacja nie otrzymała tożsamości Windows (API: ${escapeHtml(e.message)}). W IIS dla tej aplikacji włącz Windows Authentication i wyłącz Anonymous Authentication, następnie odśwież stronę.</p>`;
  }
})();
