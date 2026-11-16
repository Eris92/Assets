let teamPeople=[],teamEquipment=[],selectedPerson='',teamLoaded=false,teamDisplay='people',teamDelegations=[],teamInventoryComplete=false,teamInventoryError='';
function teamDepth(person){
  const visited=new Set();let depth=1,parent=person.manager;
  while(parent!=='me'){
    if(visited.has(parent))return 0;
    visited.add(parent);
    const manager=teamPeople.find(p=>p.id===parent);
    if(!manager)return 0;
    parent=manager.manager;depth++;
  }
  return depth;
}
function teamVisiblePeople(){
  return teamPeople.filter(p=>teamDepth(p)>0&&($('includeIndirect').checked||p.manager==='me'));
}
function renderTeam(){
  const people=teamVisiblePeople();
  if(selectedPerson&&!people.some(p=>p.id===selectedPerson))selectedPerson='';
  const equipment=teamEquipment.filter(a=>people.some(p=>p.id===a.owner));
  const missing=teamInventoryComplete?people.filter(p=>!equipment.some(a=>a.owner===p.id)):[];
  $('teamSummary').innerHTML=[['Pracownicy',people.length],
    ['Bezposredni',people.filter(p=>p.manager==='me').length],
    ['Posredni',people.filter(p=>p.manager!=='me').length],
    ['Sprzet',equipment.length],
    ['Bez przypisanego sprzetu',missing.length]].map(([label,value])=>
    '<div><strong>'+value+'</strong><span>'+label+'</span></div>').join('');
  $('teamTree').innerHTML='<h3>Struktura zespolu</h3>'+
    '<button data-person="" class="'+(!selectedPerson?'selected':'')+'">Caly zespol</button>'+
    people.map(p=>'<button style="padding-left:'+Math.min(60,14+(teamDepth(p)-1)*18)+'px" data-person="'+escapeHtml(p.id)+
      '" class="'+(selectedPerson===p.id?'selected':'')+'"><strong>'+escapeHtml(p.name)+
      '</strong><small>'+escapeHtml(p.role||'')+'</small><small>'+ (!teamInventoryComplete?'Status sprzetu: niezweryfikowany':equipment.some(a=>a.owner===p.id)?'Sprzet przypisany: '+equipment.filter(a=>a.owner===p.id).length:'BRAK PRZYPISANEGO SPRZETU - sprawdz niezgodnosc')+'</small><small>'+ 
      (p.delegated?'Dostep w zastepstwie':p.manager==='me'?'Podlega bezposrednio':'Przelozony: '+escapeHtml(teamPeople.find(m=>m.id===p.manager)?.name||''))+
      '</small></button>').join('');
  $('teamSelection').textContent=selectedPerson?(people.find(p=>p.id===selectedPerson)?.name||''):'Wszyscy pracownicy';
  const rows=equipment.filter(a=>!selectedPerson||a.owner===selectedPerson).map((a,i)=>{
    const owner=people.find(p=>p.id===a.owner);
    return equipmentRecord(a,i,owner?.name||a.owner,owner?.delegated?'W zastepstwie':
      owner?.manager==='me'?'Bezposrednia':'Posrednia');
  });
  $('teamTree').hidden=teamDisplay==='all';
  $('teamSummary').hidden=false;
  $('teamDisplayMode').value=teamDisplay;
  $('teamLayout').classList.toggle('teamAllMode',teamDisplay==='all');
  renderEquipmentList('team','teamAssets',rows,false);
  if(!teamInventoryComplete) $('teamAssets').insertAdjacentHTML('afterbegin','<p class="handoverNotice">Nie mozna potwierdzic przypisan sprzetu. '+escapeHtml(teamInventoryError||'Blad odczytu Jira.')+' Struktura pracownikow pozostaje widoczna.</p>');
  else if(missing.length) $('teamAssets').insertAdjacentHTML('afterbegin','<p class="handoverNotice"><strong>'+missing.length+' pracownikow bez przypisanego sprzetu.</strong> Wybierz osobe w strukturze i zweryfikuj brak przypisania. W razie niezgodnosci uzyj przycisku zgloszenia dodatkowego sprzetu.</p>');
  if(!people.length)$('teamAssets').innerHTML='<p class="empty">Nie odnaleziono zespolu w AD (manager) ani team.json. Sprawdz hierarchie i konto uslugi.</p>';

}
async function loadTeam(force=false){
  try{
    if(!teamLoaded||force){
      $('teamAssets').innerHTML='<p class="empty">Pobieranie sprzetu zespolu z Jira...</p>';
      const data=await api('/api/team');
      teamPeople=data.people||[];teamEquipment=data.equipment||[];teamInventoryComplete=data.inventoryComplete===true;teamInventoryError=data.inventoryError||'';teamLoaded=true;
    }
    renderTeam();
  }catch(e){$('teamAssets').innerHTML='<p class="empty">'+escapeHtml(e.message)+'</p>';if(!teamLoaded){$('teamTree').innerHTML='<h3>Struktura zespolu</h3><p class="empty">Nie mozna odczytac hierarchii AD.</p>';}}
}
let teamCanManageDelegates=false;
let delegationsGrantedToMe=[];
async function loadDelegations(){
 try {
   const data=await api('/api/team/delegations');
   teamDelegations=data.own||[];
   delegationsGrantedToMe=data.grantedToMe||[];
   teamCanManageDelegates=Boolean(data.canManage);
   renderTeamDelegations();
 } catch(error) {
   $('delegateIncoming').textContent=error.message;
   $('delegateIncoming').hidden=false;
 }
}
function renderTeamDelegations(){
 const area=$('teamDelegations');
 if(!area)return;
 $('delegatePanel').hidden=!teamCanManageDelegates;
 area.innerHTML=teamDelegations.length?
   teamDelegations.map(d=>'<div class="protocol-row"><span>'+escapeHtml(d.delegateLogin)+
     '<small>Od '+escapeHtml(new Date(d.createdAt).toLocaleDateString('pl-PL'))+
     '</small></span><button class="secondary" data-delegate-remove="'+d.id+'">Cofnij</button></div>').join(''):
   '<p class="meta">Brak aktywnych zastępców.</p>';
 const incoming=$('delegateIncoming');
 incoming.hidden=!delegationsGrantedToMe.length;
 incoming.innerHTML=delegationsGrantedToMe.length ?
   '<h3>Dostęp otrzymany w zastępstwie</h3>'+
   delegationsGrantedToMe.map(d=>'<p>'+escapeHtml(d.managerLogin)+
   ' — podgląd sprzętu i uprawnień jego zespołu (bez prawa dalszej delegacji)</p>').join('') : '';
}
$('teamTree').onclick=e=>{const b=e.target.closest('[data-person]');if(b){selectedPerson=b.dataset.person;renderTeam()}};
$('includeIndirect').onchange=renderTeam;
$('teamDisplayMode').onchange=e=>{teamDisplay=e.target.value;renderTeam()};
setupEquipmentFilters('team','teamFilters',renderTeam);
$('teamAssets').addEventListener('click',e=>{
 const button=e.target.closest('[data-team-asset]');if(!button)return;
 const asset=teamEquipment.find(a=>a.id===button.dataset.teamAsset);
 const owner=asset&&teamPeople.find(p=>p.id===asset.owner);
 if(owner)openEquipmentReport(asset,owner.name);
});
let delegateLookupTimer=null;
$('delegateLogin').addEventListener('input',e=>{
 const query=e.target.value.trim();
 clearTimeout(delegateLookupTimer);
 if(query.length<2)return;
 delegateLookupTimer=setTimeout(async()=>{
  try {
   const rows=await api('/api/team/delegates/search?q='+encodeURIComponent(query));
   if($('delegateLogin').value.trim()!==query)return;
   $('delegateSuggestions').innerHTML=rows.map(x=>'<option value="'+escapeHtml(x.login)+'" label="'+
     escapeHtml(x.Name||x.name)+'"></option>').join('');
  }catch(ex){/* search is optional; submit still validates through AD */}
 },450);
});
$('delegateForm').addEventListener('submit',async e=>{
 e.preventDefault();
 const value=$('delegateLogin').value.trim();
 if(!value)return;
 if(!confirm('Nadac zastepcy wglad do sprzetu i uprawnien Twojego zespolu?'))return;
 try{
   await api('/api/team/delegations',{method:'POST',body:JSON.stringify({login:value})});
   $('delegateLogin').value='';await loadDelegations();message('Zastępstwo zapisane.');
 }catch(ex){message(ex.message,true)}
});
$('teamDelegations').addEventListener('click',async e=>{
 const btn=e.target.closest('[data-delegate-remove]');if(!btn)return;
 if(!confirm('Cofnąć uprawnienia zastępcy?'))return;
 try{await api('/api/team/delegations/'+btn.dataset.delegateRemove,{method:'DELETE'});await loadDelegations();}
 catch(ex){message(ex.message,true)}
});
$('reportType').onchange=()=>{const edit=$('reportType').value==='attribute';
 $('attributeFields').hidden=!edit;$('attribute').required=$('proposed').required=edit;
};
