// Permission views are read-only AD memberOf snapshots. Changes are sent for
// human review, never executed against AD/Entra by the browser or backend.
let permissionSelfGroups=[];
let permissionTeamPeople=[];
let permissionTeamGroups=[];
let permissionTeamPersonGroups=[];
let permissionTeamSelected='';
let permissionTeamMode='people';
let permissionTeamOffset=null;
let permissionTeamLoadedPeople=0;
let permissionSelfVisible=[];
let permissionTeamVisible=[];
let permissionReportSelected=null;
let permissionTeamRequest=0;

function permStat(target,items){
  $(target).innerHTML=items.map(([n,v])=>
    '<div class="teamMetric"><strong>'+escapeHtml(v)+'</strong><span>'+escapeHtml(n)+'</span></div>'
  ).join('');
}
function permSort(rows,order){
  return [...rows].sort((a,b)=>{
    const k=order==='person'?'person':order==='id'?'id':'name';
    return String(a[k]||'').localeCompare(String(b[k]||''),'pl',{numeric:true})||
      String(a.name||'').localeCompare(String(b.name||''),'pl',{numeric:true});
  });
}
function renderPermissionTable(host,rows,scope){
  if(scope==='self')permissionSelfVisible=rows;
  else permissionTeamVisible=rows;
  $(host).innerHTML=rows.length?
    '<div class="equipmentTableWrap"><table class="equipmentTable"><thead><tr>'+
    '<th>Uprawnienie / grupa</th><th>Źródło</th>'+
    (scope==='team'?'<th>Pracownik</th>':'')+
    '<th class="equipmentAction">Akcja</th></tr></thead><tbody>'+
    rows.map((r,i)=>'<tr>'+
      '<td><strong class="permissionGroupName">'+escapeHtml(r.name)+'</strong>'+
      '<small class="permissionDn">'+escapeHtml(r.id)+'</small></td>'+
      '<td><span class="equipmentStatus">AD · memberOf</span><small class="permissionScope">Bezpośrednie członkostwo</small></td>'+
      (scope==='team'?'<td><strong>'+escapeHtml(r.person)+'</strong><small>'+
         escapeHtml(r.login)+'</small></td>':'')+
      '<td class="equipmentAction"><button type="button" class="reportEquipment" data-perm-scope="'+
      scope+'" data-perm-index="'+i+'">Niepotrzebne / nadmierne</button></td></tr>'
    ).join('')+'</tbody></table></div>':
    '<p class="empty">Brak grup odpowiadających filtrom lub bezpośrednich członkostw AD.</p>';
}
function renderSelfPermissions(){
  const search=$('permissionSelfSearch').value.trim().toLocaleLowerCase('pl');
  const records=permSort(permissionSelfGroups.map(g=>({
    ...g,login:me.name,person:me.name
  })).filter(g=>(g.name+' '+g.id).toLocaleLowerCase('pl').includes(search)),
  $('permissionSelfSort').value);
  permStat('permissionSelfStats',[
    ['Bezpośrednie grupy AD',permissionSelfGroups.length],
    ['Widoczne po filtrze',records.length]
  ]);
  $('permissionSelfCount').textContent='Widoczne: '+records.length+' z '+permissionSelfGroups.length+
    ' · pełne uprawnienia efektywne wymagają dodatkowych źródeł';
  renderPermissionTable('myPermissionsRows',records,'self');
}
async function loadMyPermissions(){
  $('myPermissionsRows').innerHTML='<p class="empty">Pobieranie uprawnień z AD...</p>';
  try{
    const response=await api('/api/permissions');
    permissionSelfGroups=response.groups||[];
    renderSelfPermissions();
  }catch(e){
    permissionSelfGroups=[];renderSelfPermissions();
    $('myPermissionsRows').textContent=e.message;
  }
}
function permTeamSelection(){
  const selector=$('teamPermissionsPerson');
  const current=selector.value||permissionTeamSelected;
  selector.innerHTML=permissionTeamPeople.map(p=>
    '<option value="'+escapeHtml(p.login)+'">'+escapeHtml(p.name)+' ('+
    escapeHtml(p.login)+')</option>').join('');
  if(permissionTeamPeople.some(p=>p.login===current))selector.value=current;
  permissionTeamSelected=selector.value;
}
async function loadTeamPermissions(){
  if(!me?.manager)return;
  $('teamPermissionsRows').innerHTML='<p class="empty">Pobieranie listy zespołu z AD...</p>';
  try{
    permissionTeamPeople=await api('/api/team/members');
    permTeamSelection();
    await refreshTeamPermissions();
  }catch(e){
    $('teamPermissionsRows').textContent=e.message;
  }
}
function renderTeamPermissions(){
  const all=permissionTeamMode==='all';
  $('teamPermissionsMode').value=permissionTeamMode;
  $('teamPermissionsPersonLabel').hidden=all;
  const search=$('teamPermissionsSearch').value.trim().toLocaleLowerCase('pl');
  const entries=all?permissionTeamGroups:permissionTeamPersonGroups;
  const rows=permSort(entries.filter(g=>
    [g.name,g.id,g.login,g.person].join(' ').toLocaleLowerCase('pl').includes(search)),
    $('teamPermissionsSort').value);
  permStat('teamPermissionsStats',[
    ['Pracownicy w zakresie',permissionTeamPeople.length],
    [all?'Pracownicy załadowani':'Wybrany pracownik',all?permissionTeamLoadedPeople:(permissionTeamSelected?'1':'0')],
    ['Grupy na liście',entries.length],
    ['Widoczne po filtrze',rows.length]
  ]);
  $('teamPermissionsCount').textContent=all?
    'Widoczne '+rows.length+' z '+entries.length+' załadowanych uprawnień. '+
    'Przeszukujesz '+permissionTeamLoadedPeople+' z '+permissionTeamPeople.length+' pracowników.' :
    'Widoczne '+rows.length+' z '+entries.length+' bezpośrednich grup pracownika.';
  renderPermissionTable('teamPermissionsRows',rows,'team');
  $('teamPermissionsPager').hidden=!all||permissionTeamOffset===null;
  $('teamPermissionsPageInfo').textContent='Załadowano '+permissionTeamLoadedPeople+
    ' z '+permissionTeamPeople.length+' pracowników.';
}
async function refreshTeamPermissions(){
  if(!me?.manager)return;
  const current=++permissionTeamRequest;
  if(permissionTeamMode==='all'){
    permissionTeamGroups=[];permissionTeamOffset=0;permissionTeamLoadedPeople=0;
    renderTeamPermissions();
    return fetchMoreTeamPermissions(current);
  }
  const login=$('teamPermissionsPerson').value;
  permissionTeamSelected=login;
  if(!login){permissionTeamPersonGroups=[];renderTeamPermissions();return}
  $('teamPermissionsRows').innerHTML='<p class="empty">Pobieranie grup AD...</p>';
  try{
    const data=await api('/api/team/permissions?login='+encodeURIComponent(login));
    if(current!==permissionTeamRequest)return;
    const person=permissionTeamPeople.find(p=>p.login===login);
    permissionTeamPersonGroups=(data.groups||[]).map(g=>({
      id:g.id,name:g.name,login,person:person?.name||login
    }));
    renderTeamPermissions();
  }catch(e){
    if(current!==permissionTeamRequest)return;
    $('teamPermissionsRows').textContent=e.message;
  }
}
async function fetchMoreTeamPermissions(current=permissionTeamRequest){
  if(permissionTeamMode!=='all'||permissionTeamOffset===null||current!==permissionTeamRequest)return;
  const button=$('teamPermissionsMore');
  button.disabled=true;
  $('teamPermissionsCount').textContent='Ładowanie kolejnej partii uprawnień z AD...';
  try{
    const data=await api('/api/team/permissions/all?offset='+permissionTeamOffset);
    if(current!==permissionTeamRequest)return;
    permissionTeamGroups.push(...(data.items||[]));
    permissionTeamLoadedPeople+=data.peopleLoaded||0;
    permissionTeamOffset=data.nextOffset??null;
    renderTeamPermissions();
  }catch(e){
    if(current!==permissionTeamRequest)return;
    $('teamPermissionsCount').textContent='Błąd pobierania: '+e.message+
      ' · można ponowić ładowanie, bez ukrywania załadowanych danych.';
  }finally{button.disabled=false}
}
function reportPermissionDialog(scope,row=null){
  if(!me?.allowed)return;
  permissionReportSelected=row?{...row}:null;
  const group=Boolean(row);
  const eligible=scope==='team'&&me?.manager;
  const people=eligible?permissionTeamPeople:[{login:me.name,name:me.name}];
  if(!people.length){message('Brak osób w autoryzowanym zakresie.',true);return}
  const selector=$('permissionReportEmployee');
  selector.innerHTML=people.map(p=>'<option value="'+escapeHtml(p.login)+'">'+
    escapeHtml(p.name)+'</option>').join('');
  if(row&&people.some(p=>p.login===row.login))selector.value=row.login;
  else if(eligible&&people.some(p=>p.login===permissionTeamSelected))
    selector.value=permissionTeamSelected;
  selector.disabled=group;
  $('permissionEmployeeLabel').hidden=!eligible;
  $('permissionReportTitle').textContent=group?'Zgłoś zbędne / nadmierne uprawnienie':
    'Zgłoś inny dostęp niewidoczny w AD';
  $('permissionReportContext').textContent=group?
    'Uprawnienie: '+row.name+' | '+row.id:
    'Jeśli dostęp nie jest widoczny na liście grup AD, zgłoś go do ręcznej kontroli.';
  $('permissionReportKind').value=group?'unneeded':'unlisted';
  $('permissionReportKind').querySelector('option[value="unneeded"]').hidden=!group;
  $('permissionReportKind').querySelector('option[value="excessive"]').hidden=!group;
  $('permissionReportKind').querySelector('option[value="unlisted"]').hidden=group;
  $('permissionExternalLabel').hidden=group;
  $('permissionExternal').required=!group;
  $('permissionExternal').value='';
  $('permissionReportReason').value='';
  let error=$('permissionReportError');
  if(!error){
    error=document.createElement('p');error.id='permissionReportError';
    error.setAttribute('role','status');
    $('permissionReportForm').querySelector('.actions').before(error);
  }
  error.textContent='';
  $('permissionReportDialog').showModal();
}
async function submitPermissionReport(e){
  e.preventDefault();
  const kind=$('permissionReportKind').value;
  const login=$('permissionReportEmployee').value;
  const reason=$('permissionReportReason').value.trim();
  const externalAccess=$('permissionExternal').value.trim();
  const isUnlisted=kind==='unlisted';
  if(reason.length<10 || (isUnlisted&&externalAccess.length<3)){
    $('permissionReportError').textContent='Podaj dokładne uprawnienie i uzasadnienie (minimum 10 znaków).';
    return;
  }
  const button=$('permissionReportSubmit');button.disabled=true;
  try{
    const response=await api('/api/reports/permissions',{
      method:'POST',
      body:JSON.stringify({
        employeeLogin:login,
        kind,
        groupDn:isUnlisted?'':permissionReportSelected?.id||'',
        externalAccess:isUnlisted?externalAccess:'',
        reason
      })
    });
    $('permissionReportDialog').close();
    view('reports');
    await loadReports();
    message('Zgłoszenie #'+response.id+' zapisane. Administrator zweryfikuje uprawnienie bez automatycznego odbierania dostępu.');
  }catch(err){
    $('permissionReportError').textContent=err.message;
  }finally{button.disabled=false}
}

$('permissionSelfSearch').addEventListener('input',renderSelfPermissions);
$('permissionSelfSort').addEventListener('change',renderSelfPermissions);
$('permissionSelfClear').addEventListener('click',()=>{
  $('permissionSelfSearch').value='';$('permissionSelfSort').value='name';
  renderSelfPermissions();
});
$('permissionSelfRefresh').addEventListener('click',loadMyPermissions);
$('permissionOtherSelf').addEventListener('click',()=>reportPermissionDialog('self'));
$('permissionOtherTeam').addEventListener('click',()=>reportPermissionDialog('team'));
$('teamPermissionsRefresh').addEventListener('click',refreshTeamPermissions);
$('teamPermissionsPerson').addEventListener('change',refreshTeamPermissions);
$('teamPermissionsMode').addEventListener('change',e=>{
  permissionTeamMode=e.target.value;
  refreshTeamPermissions();
});
$('teamPermissionsSearch').addEventListener('input',renderTeamPermissions);
$('teamPermissionsSort').addEventListener('change',renderTeamPermissions);
$('teamPermissionsMore').addEventListener('click',()=>fetchMoreTeamPermissions());
document.addEventListener('click',e=>{
  const button=e.target.closest('[data-perm-scope][data-perm-index]');
  if(!button)return;
  const scope=button.dataset.permScope;
  const index=Number(button.dataset.permIndex);
  const rows=scope==='self'?permissionSelfVisible:permissionTeamVisible;
  const row=rows[index];
  if(!row)return;
  reportPermissionDialog(scope,row);
});
$('permissionReportKind').addEventListener('change',e=>{
  const other=e.target.value==='unlisted';
  $('permissionExternalLabel').hidden=!other;
  $('permissionExternal').required=other;
});
$('permissionReportClose').onclick=$('permissionReportCancel').onclick=
  ()=>$('permissionReportDialog').close();
$('permissionReportForm').addEventListener('submit',submitPermissionReport);
