let teamPeople = [], teamEquipment = [], selectedPerson = '', teamLoaded = false;
function teamDepth(person) {
  const visited=new Set(); let depth=1, parent=person.manager;
  while(parent!=='me') {
    if(visited.has(parent)) return 0;
    visited.add(parent);
    const manager=teamPeople.find(p=>p.id===parent);
    if(!manager) return 0;
    parent=manager.manager; depth++;
  }
  return depth;
}
function renderTeam() {
  const people=teamPeople.filter(p=>teamDepth(p)>0 && ($('includeIndirect').checked || p.manager==='me'));
  if(selectedPerson && !people.some(p=>p.id===selectedPerson)) selectedPerson='';
  const equipment=teamEquipment.filter(a=>people.some(p=>p.id===a.owner));
  $('teamSummary').innerHTML=[['Pracownicy',people.length],['Bezposredni',people.filter(p=>p.manager==='me').length],['Posredni',people.filter(p=>p.manager!=='me').length],['Sprzet',equipment.length]].map(([label,value])=>`<div><strong>${value}</strong><span>${label}</span></div>`).join('');
  $('teamTree').innerHTML='<h3>Struktura zespolu</h3><button data-person="" class="'+(!selectedPerson?'selected':'')+'">Caly zespol</button>'+people.map(p=>`<button style="padding-left:${14+(teamDepth(p)-1)*18}px" data-person="${escapeHtml(p.id)}" class="${selectedPerson===p.id?'selected':''}"><strong>${escapeHtml(p.name)}</strong><small>${escapeHtml(p.role || '')}</small><small>${p.manager==='me'?'Bezposrednio pod Toba':'Przelozony: '+escapeHtml(teamPeople.find(m=>m.id===p.manager)?.name || '')}</small></button>`).join('');
  $('teamSelection').textContent=selectedPerson?people.find(p=>p.id===selectedPerson).name:'Wszyscy pracownicy';
  const rows=equipment.filter(a=>!selectedPerson||a.owner===selectedPerson).map((a,index)=>{
    const owner=people.find(p=>p.id===a.owner);
    return equipmentRecord(a,index,owner.name,owner.manager==='me'?'Bezposrednia':'Posrednia - poziom '+teamDepth(owner));
  });
  renderEquipmentList('team','teamAssets',rows,false);
  if(!teamPeople.length) $('teamAssets').innerHTML='<p class="empty">Brak podwladnych. Hierarchia zespolu wymaga konfiguracji team.json na serwerze.</p>';
}
async function loadTeam(force = false) {
  try {
    if(!teamLoaded || force) {
      $('teamAssets').innerHTML='<p class="empty">Pobieranie sprzetu zespolu...</p>';
      const data=await api('/api/team');
      teamPeople=data.people; teamEquipment=data.equipment; teamLoaded=true;
    }
    renderTeam();
  } catch(e) { $('teamAssets').innerHTML=`<p class="empty">${escapeHtml(e.message)}</p>`; }
}
$('teamTree').onclick=e=>{const b=e.target.closest('[data-person]');if(b){selectedPerson=b.dataset.person;renderTeam();}};
$('includeIndirect').onchange=renderTeam;
setupEquipmentFilters('team','teamFilters',renderTeam);
$('teamAssets').addEventListener('click',e=>{
  const button=e.target.closest('[data-team-asset]');if(!button)return;
  const asset=teamEquipment.find(a=>a.id===button.dataset.teamAsset);
  const owner=asset && teamPeople.find(p=>p.id===asset.owner);
  if(owner)openEquipmentReport(asset,owner.name);
});
$('reportType').onchange=()=>{const edit=$('reportType').value==='attribute';$('attributeFields').hidden=!edit;$('attribute').required=$('proposed').required=edit;};
