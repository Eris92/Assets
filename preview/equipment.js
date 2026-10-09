function equipmentAttribute(asset, name) {
  return asset.attributes?.find(a => a.name === name)?.value || '';
}
function equipmentRecord(asset, index, owner = '', relation = '') {
  return {id:asset.id, index, name:asset.name, key:asset.key, type:asset.type || equipmentAttribute(asset,'Typ') || 'Inny', serial:asset.serial || equipmentAttribute(asset,'Numer seryjny'), status:asset.status || equipmentAttribute(asset,'Status'), location:asset.location || equipmentAttribute(asset,'Lokalizacja'), owner, relation};
}
function equipmentFilters(prefix) {
  return `<div class="equipmentFilters"><label class="searchFilter">Szukaj<input id="${prefix}Search" type="search" placeholder="Nazwa, ID, numer seryjny lub osoba"></label><label>Typ<select id="${prefix}Type"><option value="">Wszystkie typy</option></select></label><label>Status<select id="${prefix}Status"><option value="">Wszystkie statusy</option></select></label><label>Lokalizacja<select id="${prefix}Location"><option value="">Wszystkie lokalizacje</option></select></label><label>Sortowanie<select id="${prefix}Sort"><option value="name">Nazwa A-Z</option><option value="key">ID A-Z</option><option value="type">Typ A-Z</option><option value="owner">Osoba A-Z</option></select></label><button class="secondary" id="${prefix}Reset">Wyczysc filtry</button></div><p class="equipmentCount" id="${prefix}Count" role="status" aria-live="polite"></p>`;
}
function setupEquipmentFilters(prefix, host, render) {
  $(host).innerHTML = equipmentFilters(prefix);
  $(prefix+'Search').addEventListener('input', render);
  for (const key of ['Type','Status','Location','Sort']) $(prefix+key).addEventListener('change', render);
  $(prefix+'Reset').addEventListener('click', () => {
    for (const key of ['Search','Type','Status','Location']) $(prefix+key).value = '';
    $(prefix+'Sort').value = 'name';
    render();
  });
}
function updateEquipmentOptions(prefix, records) {
  for (const [key, field, label] of [['Type','type','Wszystkie typy'],['Status','status','Wszystkie statusy'],['Location','location','Wszystkie lokalizacje']]) {
    const select = $(prefix+key), selected = select.value;
    const values = [...new Set(records.map(r=>r[field]).filter(Boolean))].sort((a,b)=>a.localeCompare(b,'pl'));
    select.innerHTML = `<option value="">${label}</option>` + values.map(v=>`<option value="${escapeHtml(v)}">${escapeHtml(v)}</option>`).join('');
    select.value = values.includes(selected) ? selected : '';
  }
}
function filterEquipment(records, filters) {
  const search = (filters.search || '').trim().toLocaleLowerCase('pl');
  return records.filter(r => (!filters.type || r.type===filters.type) && (!filters.status || r.status===filters.status) && (!filters.location || r.location===filters.location) && Object.values(r).join(' ').toLocaleLowerCase('pl').includes(search)).sort((a,b)=>String(a[filters.sort] || '').localeCompare(String(b[filters.sort] || ''),'pl',{numeric:true}) || String(a.key).localeCompare(String(b.key),'pl',{numeric:true}));
}
function renderEquipmentList(prefix, host, records, personal) {
  updateEquipmentOptions(prefix, records);
  const rows = filterEquipment(records, {search:$(prefix+'Search').value,type:$(prefix+'Type').value,status:$(prefix+'Status').value,location:$(prefix+'Location').value,sort:$(prefix+'Sort').value});
  $(prefix+'Count').textContent = `Widoczne: ${rows.length} z ${records.length}`;
  if (!rows.length) { $(host).innerHTML = '<p class="empty">Brak sprzetu spelniajacego wybrane filtry.</p>'; return; }
  $(host).innerHTML = `<div class="equipmentTableWrap"><table class="equipmentTable"><thead><tr><th scope="col">Sprzet / ID</th><th scope="col">Typ</th><th scope="col">Numer seryjny</th><th scope="col">Status</th><th scope="col">Lokalizacja</th>${personal?'':'<th scope="col">Pracownik / podleglosc</th>'}<th scope="col" class="equipmentAction">Akcja</th></tr></thead><tbody>${rows.map(r=>`<tr><td><strong>${escapeHtml(r.name)}</strong><small>${escapeHtml(r.key)}</small></td><td>${escapeHtml(r.type)}</td><td>${escapeHtml(r.serial || '-')}</td><td><span class="equipmentStatus">${escapeHtml(r.status || '-')}</span></td><td>${escapeHtml(r.location || '-')}</td>${personal?'':`<td><strong>${escapeHtml(r.owner)}</strong><small>${escapeHtml(r.relation)}</small></td>`}<td class="equipmentAction"><button class="reportEquipment" ${personal?`data-asset="${r.index}"`:`data-team-asset="${escapeHtml(r.id)}"`}>Zg&#322;o&#347; niezgodno&#347;&#263;</button></td></tr>`).join('')}</tbody></table></div>`;
}
function renderPersonalEquipment() {
  renderEquipmentList('asset','assets',assets.map((asset,index)=>equipmentRecord(asset,index)),true);
}
