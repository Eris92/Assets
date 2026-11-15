// Unified entitlement inventory. Browser never accesses Graph or Exchange
// directly: authorization, filtering and connector credentials remain server-side.
const entitlementCategories=[
  ['all','Wszystkie'],['LocalAD','Lokalne AD'],['Entra','Entra ID'],
  ['Exchange','Poczta / Exchange'],['Files','Pliki SMB/NTFS'],
  ['SharePoint','SharePoint'],['Teams','Teams'],['M365License','Licencje M365']
];
const entState={
  self:null,teamPeople:[],teamCurrent:'',teamAll:false,teamData:[],
  teamLoaded:0,category:'all',query:'',teamCategory:'all',teamQuery:'',
  teamRequest:0,editing:null,report:null,adminRules:[],
  loading:false
};
const entHtml=s=>escapeHtml(s??'');
function entCategoryLabel(key){return entitlementCategories.find(c=>c[0]===key)?.[1]||key}
function entCategoryOptions(selected){
 return entitlementCategories.map(([v,label])=>'<option value="'+v+'" '+(v===selected?'selected':'')+'>'+entHtml(label)+'</option>').join('');
}
function entStatusInfo(data){
 if(!data?.sources)return '';
 return '<details class="entStatus"><summary>Stan źródeł / jakość danych</summary>'+
  Object.entries(data.sources).map(([src,status])=>'<div><b>'+entHtml(entCategoryLabel(src))+
  ':</b> '+entHtml(status)+'</div>').join('')+
  '<p class="meta">'+entHtml(data.warning||'')+'</p></details>';
}
function entRowTable(rows,admin=false){
 if(!rows.length)return '<p class="empty">Brak uprawnień w wybranym filtrze. Sprawdź stan źródeł powyżej.</p>';
 return '<div class="equipmentTableWrap"><table class="equipmentTable"><thead><tr>'+
   '<th>Uprawnienie</th><th>Źródło / sposób nadania</th><th>Opis przeznaczenia</th>'+
   '<th>Osoba</th><th class="equipmentAction">Akcje</th></tr></thead><tbody>'+
   rows.map((item,i)=>
    '<tr class="'+(item.hidden?'entHidden':'')+'"><td><strong>'+entHtml(item.name)+
    '</strong><small>'+entHtml(item.kind)+': '+entHtml(item.details||item.key)+
    '</small>'+(item.hidden?'<small>Ukryte przed pracownikami</small>':'')+'</td>'+
    '<td><b>'+entHtml(entCategoryLabel(item.source))+'</b><small>'+
    entHtml(item.via)+(item.stale?' · starszy snapshot':'')+'</small></td>'+
    '<td>'+ (item.note?'<span>'+entHtml(item.note)+'</span>':'<span class="meta">Brak opisu administratora</span>')+
    '</td><td><small>'+entHtml(item.userLogin)+'</small></td>'+
    '<td class="equipmentAction"><div class="entActions">'+
    '<button type="button" class="reportEquipment" data-ent-report="'+i+'">Zgłoś nadmiar</button>'+
    (admin?'<button type="button" class="secondary" data-ent-edit="'+i+'">Opis / ukrycie</button>':'')+
    '</div></td></tr>'
   ).join('')+'</tbody></table></div>';
}
function entFilter(all,cat,query){
 const q=query.toLocaleLowerCase('pl').trim();
 return all.filter(x=>(cat==='all'||x.source===cat) &&
  [x.name,x.key,x.kind,x.details,x.via,x.note,x.userLogin].join(' ').toLocaleLowerCase('pl').includes(q))
 .sort((a,b)=>a.source.localeCompare(b.source)||a.name.localeCompare(b.name,'pl')||
   a.userLogin.localeCompare(b.userLogin));
}
function entStats(items,shown){
 const distinct=new Set(items.map(x=>x.source)).size;
 return '<div class="accessStats"><div class="teamMetric"><strong>'+items.length+
  '</strong><span>Pozycji</span></div><div class="teamMetric"><strong>'+shown.length+
  '</strong><span>Po filtrach</span></div><div class="teamMetric"><strong>'+
  distinct+'</strong><span>Źródeł z danymi</span></div></div>';
}
function renderEntitlementSelf(){
 const root=$('entitlementSelfRoot');if(!root)return;
 const all=entState.self?.items||[];
 const filtered=entFilter(all,entState.category,entState.query);
 root.innerHTML=entStats(all,filtered)+
 '<div class="entControls"><label>Źródło<select id="entSelfCategory">'+
 entCategoryOptions(entState.category)+'</select></label>'+
 '<label class="searchFilter">Szukaj dostępu<input id="entSelfQuery" placeholder="VPN, Send As, SharePoint, E3..." value="'+entHtml(entState.query)+'"></label>'+
 '<button type="button" class="secondary" id="entSelfOther">+ Zgłoś inny dostęp</button>'+
 '<button type="button" class="secondary" id="entSelfRefresh">Odśwież</button></div>'+
 entStatusInfo(entState.self)+
 '<p class="equipmentCount">Widoczne: '+filtered.length+' z '+all.length+'</p>'+
 entRowTable(filtered,Boolean(me?.admin));
 entState.renderedSelf=filtered;
}
async function loadMyPermissions(){
 const target=$('entitlementSelfRoot');
 if(!target)return;
 target.innerHTML='<p class="empty">Odczyt uprawnień AD i skonfigurowanych konektorów...</p>';
 try{
   entState.self=await api('/api/entitlements');
   renderEntitlementSelf();
 }catch(ex){target.textContent=ex.message}
}
async function entTeamMembers(){
 if(entState.teamPeople.length)return entState.teamPeople;
 entState.teamPeople=await api('/api/team/members');
 if(!entState.teamCurrent)entState.teamCurrent=entState.teamPeople[0]?.login||'';
 return entState.teamPeople;
}
async function loadTeamPermissions(){
 if(!me?.manager)return;
 try{
  await entTeamMembers();
  await refreshTeamPermissions();
 }catch(ex){$('entitlementTeamRoot').textContent=ex.message}
}
async function refreshTeamPermissions(){
 if(!me?.manager)return;
 const request=++entState.teamRequest;
 const target=$('entitlementTeamRoot');
 target.innerHTML='<p class="empty">Odczyt uprawnień z autoryzowanego zakresu AD...</p>';
 try{
  await entTeamMembers();
  if(entState.teamAll){
   entState.teamData=[];entState.teamLoaded=0;
   await entLoadTeamBatch(request);
  }else{
   if(!entState.teamCurrent)return;
   const data=await api('/api/entitlements?login='+encodeURIComponent(entState.teamCurrent));
   if(request!==entState.teamRequest)return;
   entState.teamData=[data];entState.teamLoaded=1;
  }
  if(request===entState.teamRequest)renderEntitlementTeam();
 }catch(ex){if(request===entState.teamRequest)target.textContent=ex.message}
}
async function entLoadTeamBatch(request=entState.teamRequest){
 const next=entState.teamPeople.slice(entState.teamLoaded,entState.teamLoaded+3);
 if(!next.length)return;
 const result=[];
 for(const p of next){
   try{result.push(await api('/api/entitlements?login='+encodeURIComponent(p.login)))}
   catch(e){result.push({login:p.login,items:[],sources:{error:e.message},warning:'Odczyt niekompletny'})}
   if(request!==entState.teamRequest)return;
 }
 entState.teamData.push(...result);
 entState.teamLoaded+=next.length;
}
function renderEntitlementTeam(){
 const root=$('entitlementTeamRoot');if(!root)return;
 const all=entState.teamData.flatMap(d=>d.items||[]);
 const rows=entFilter(all,entState.teamCategory,entState.teamQuery);
 const people=entState.teamPeople;
 root.innerHTML=entStats(all,rows)+
 '<div class="entControls"><label>Widok<select id="entTeamMode"><option value="people" '+
 (!entState.teamAll?'selected':'')+'>Pracownik</option><option value="all" '+
 (entState.teamAll?'selected':'')+'>Cały zespół</option></select></label>'+
 (!entState.teamAll?'<label>Pracownik<select id="entTeamPerson">'+people.map(p=>
  '<option value="'+entHtml(p.login)+'" '+(p.login===entState.teamCurrent?'selected':'')+
  '>'+entHtml(p.name)+'</option>').join('')+'</select></label>':'')+
 '<label>Źródło<select id="entTeamCategory">'+entCategoryOptions(entState.teamCategory)+'</select></label>'+
 '<label class="searchFilter">Szukaj uprawnienia<input id="entTeamQuery" placeholder="Send As, Teams, Office..." value="'+entHtml(entState.teamQuery)+'"></label>'+
 '<button type="button" class="secondary" id="entTeamRefresh">Odśwież</button></div>'+
 entState.teamData.map(d=>entStatusInfo(d)).join('')+
 '<p class="equipmentCount">Wyniki: '+rows.length+' (dane '+entState.teamLoaded+' z '+
 people.length+' pracowników)</p>'+entRowTable(rows,Boolean(me?.admin))+
 (entState.teamAll&&entState.teamLoaded<people.length?
 '<div class="actions"><button type="button" class="secondary" id="entTeamNext">Pobierz kolejnych 3 pracowników</button></div>':'');
 entState.renderedTeam=rows;
}
function entPickRow(button){
 const root=button.closest('#entitlementSelfRoot,#entitlementTeamRoot');
 const self=root?.id==='entitlementSelfRoot';
 const index=Number(button.dataset.entReport??button.dataset.entEdit);
 if(!Number.isInteger(index)||index<0)return null;
 return (self?entState.renderedSelf:entState.renderedTeam)?.[index]||null;
}
function entEditDialog(row){
 if(!me?.admin)return;
 entState.editing=row;
 $('entEditSource').value=row.source;
 $('entEditKey').value=row.key;
 $('entEditTitle').textContent=row.name+' · '+entCategoryLabel(row.source);
 $('entEditNote').value=row.note||'';
 $('entEditHidden').checked=Boolean(row.hidden);
 $('entEditDialog').showModal();
}
async function entSaveMetadata(e){
 e.preventDefault();if(!me?.admin||!entState.editing)return;
 const button=$('entEditSave');button.disabled=true;
 try{
  await api('/api/admin/entitlements/metadata',{method:'PUT',body:JSON.stringify({
   source:entState.editing.source,key:entState.editing.key,
   note:$('entEditNote').value,hidden:$('entEditHidden').checked
  })});
  $('entEditDialog').close();
  message('Opis i widoczność uprawnienia zapisane.');
  await Promise.all([loadMyPermissions(),me.manager?refreshTeamPermissions():Promise.resolve()]);
  await loadEntitlementAdmin();
 }catch(ex){message(ex.message,true)}
 finally{button.disabled=false}
}
function entReportDialog(row){
 if(!row)return;
 entState.report=row;
 $('entReportHeading').textContent='Zgłoszenie: '+row.name;
 $('entReportIdentity').textContent=entCategoryLabel(row.source)+' | '+row.userLogin+
   ' | '+row.details;
 $('entReportWhy').value='';
 $('entReportKind').value='unneeded';
 $('entReportDialog').showModal();
}
async function entSubmitReport(e){
 e.preventDefault();
 const row=entState.report;if(!row)return;
 const reason=$('entReportWhy').value.trim();
 if(reason.length<10){message('Wpisz uzasadnienie liczące co najmniej 10 znaków.',true);return}
 const button=$('entReportSend');button.disabled=true;
 try{
  await api('/api/reports/entitlements',{method:'POST',body:JSON.stringify({
   employeeLogin:row.userLogin,source:row.source,key:row.key,
   kind:$('entReportKind').value,reason
  })});
  $('entReportDialog').close();
  view('reports');await loadReports();
  message('Zgłoszenie trafiło do oceny administratora; nie odebrano automatycznie dostępu.');
 }catch(ex){message(ex.message,true)}
 finally{button.disabled=false}
}
async function loadEntitlementAdmin(){
 if(!me?.admin)return;
 try {
  const [rules,graph]=await Promise.all([
    api('/api/admin/entitlements/metadata'),api('/api/admin/entitlements/graph')
  ]);
  entState.adminRules=rules;
  $('entGraphTenant').value=graph.tenantId||'';
  $('entGraphClient').value=graph.clientId||'';
  $('entGraphThumb').value=graph.certificateThumbprint||'';
  $('entGraphStatus').textContent=graph.configured?
    'Skonfigurowano. Certyfikat musi mieć dostępną część prywatną dla App Pool.':
    'Brak połączenia. Wprowadź dane Entra App Registration i certyfikatu.';
  renderEntRules();
 }catch(ex){$('entRules').textContent=ex.message}
}
function renderEntRules(){
 $('entRules').innerHTML=entState.adminRules.length?
  entState.adminRules.map((r,i)=>'<div class="protocol-row"><div><b>'+
   entHtml(r.source)+'</b> · '+entHtml(r.key)+
   '<small>'+entHtml(r.note||'Bez opisu')+' · '+(r.hidden?'Ukryte':'Widoczne')+'</small></div>'+
   '<button type="button" class="secondary" data-ent-rule="'+i+'">Edytuj</button></div>').join(''):
  '<p class="empty">Brak ustawień opisów / ukryć. Otwórz dowolną grupę jako administrator i użyj „Opis / ukrycie”.</p>';
}
function entMetaManual(rule=null){
 entState.editing=rule?{source:rule.source,key:rule.key,name:rule.key,
     note:rule.note,hidden:rule.hidden}:null;
 $('entManualSource').value=rule?.source||'LocalAD';
 $('entManualKey').value=rule?.key||'';
 $('entManualNote').value=rule?.note||'';
 $('entManualHidden').checked=Boolean(rule?.hidden);
 $('entManualDialog').showModal();
}
async function entManualSave(e){
 e.preventDefault();
 try{
  await api('/api/admin/entitlements/metadata',{method:'PUT',body:JSON.stringify({
   source:$('entManualSource').value,key:$('entManualKey').value,
   note:$('entManualNote').value,hidden:$('entManualHidden').checked
  })});
  $('entManualDialog').close();
  await loadEntitlementAdmin();
  message('Zasada widoczności i opis zapisane.');
 }catch(err){message(err.message,true)}
}
async function entGraphSave(e){
 e.preventDefault();
 const b=$('entGraphSave');b.disabled=true;
 try{
  await api('/api/admin/entitlements/graph',{method:'POST',body:JSON.stringify({
   tenantId:$('entGraphTenant').value.trim(),
   clientId:$('entGraphClient').value.trim(),
   certificateThumbprint:$('entGraphThumb').value.replace(/\s/g,'')
  })});
  await loadEntitlementAdmin();
  message('Konfiguracja Graph zapisana (certyfikat pozostaje w Windows Cert Store).');
 }catch(ex){message(ex.message,true)}
 finally{b.disabled=false}
}
async function entImportSave(e){
 e.preventDefault();
 let payload;
 try{payload=JSON.parse($('entImportJson').value)}catch{message('Niepoprawny JSON.',true);return}
 if(!confirm('Zastąpić snapshot wskazanego użytkownika i źródła?'))return;
 try{
  const result=await api('/api/admin/entitlements/import',{
   method:'POST',body:JSON.stringify(payload)});
  message('Snapshot '+result.source+' / '+result.user+' — '+result.imported+' wpisów.');
 }catch(ex){message(ex.message,true)}
}
document.addEventListener('input',e=>{
 if(e.target.id==='entSelfQuery'){entState.query=e.target.value;renderEntitlementSelf();
  $('entSelfQuery').focus();$('entSelfQuery').setSelectionRange(e.target.selectionStart,e.target.selectionEnd)}
 if(e.target.id==='entTeamQuery'){entState.teamQuery=e.target.value;renderEntitlementTeam();
  $('entTeamQuery').focus();$('entTeamQuery').setSelectionRange(e.target.selectionStart,e.target.selectionEnd)}
});
document.addEventListener('change',e=>{
 const id=e.target.id;
 if(id==='entSelfCategory'){entState.category=e.target.value;renderEntitlementSelf()}
 else if(id==='entTeamCategory'){entState.teamCategory=e.target.value;renderEntitlementTeam()}
 else if(id==='entTeamMode'){entState.teamAll=e.target.value==='all';refreshTeamPermissions()}
 else if(id==='entTeamPerson'){entState.teamCurrent=e.target.value;refreshTeamPermissions()}
});
document.addEventListener('click',async e=>{
 const target=e.target.closest('button');if(!target)return;
 if(target.id==='entSelfRefresh')loadMyPermissions();
 else if(target.id==='entTeamRefresh')refreshTeamPermissions();
 else if(target.id==='entTeamNext'){
  const button=target;button.disabled=true;
  try{await entLoadTeamBatch();renderEntitlementTeam()}
  catch(ex){message(ex.message,true)}finally{button.disabled=false}
 }
 else if(target.dataset.entReport!==undefined){
  const row=entPickRow(target);if(row)entReportDialog(row)
 }
 else if(target.dataset.entEdit!==undefined){
  const row=entPickRow(target);if(row)entEditDialog(row)
 }
 else if(target.dataset.entRule!==undefined){
  const rule=entState.adminRules[Number(target.dataset.entRule)];
  if(rule)entMetaManual(rule);
 }
 else if(target.id==='entManualNew')entMetaManual();
 else if(target.id==='entEditClose')$('entEditDialog').close();
 else if(target.id==='entReportClose')$('entReportDialog').close();
});
$('entEditForm').addEventListener('submit',entSaveMetadata);
$('entReportForm').addEventListener('submit',entSubmitReport);
$('entManualForm').addEventListener('submit',entManualSave);
$('entGraphForm').addEventListener('submit',entGraphSave);
$('entImportForm').addEventListener('submit',entImportSave);
$('entEditCancel').onclick=()=>$('entEditDialog').close();
$('entManualCancel').onclick=()=>$('entManualDialog').close();
$('entReportCancel').onclick=()=>$('entReportDialog').close();
