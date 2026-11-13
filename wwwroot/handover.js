// Operator-only handover UI. API enforces Windows AD group permissions.
const handover = {
  mode:'return', source:null, target:null, intermediate:null, evidenceMode:'hybrid', assets:[], selected:new Map(), protocols:[],
  results:{source:[],target:[],intermediate:[]}, queries:{source:'',target:'',intermediate:'',asset:'',notes:''},
  timers:{}, busy:false, request:0
};
function hEsc(value){ return escapeHtml(value??''); }
function hStatus(value,error=false){
  const node=$('handoverStatus'); if(!node)return;
  node.textContent=value; node.style.color=error?'#ad413b':'#216c68';
  node.hidden=!value;
}
function hPerson(role){
  const person=handover[role];
  return person?'<div class="handover-selected">'+hEsc(person.name)+' · '+hEsc(person.login)+
    '<button type="button" data-h-clear="'+role+'">Usuń</button></div>':
    '<div class="meta">Wybierz pracownika z wyników wyszukiwania.</div>';
}
function hRenderHistory(){
  const target=$('handoverHistory'); if(!target)return;
  target.innerHTML=handover.protocols.length?handover.protocols.slice(0,20).map(p=>
    '<div class="protocol-row"><div><strong>ASSETS-'+p.id+' · '+(p.mode==='return'?'Zwrot':'Przekazanie')+'</strong>'+
    '<small>'+hEsc(p.sourceName||'Magazyn')+' → '+hEsc(p.targetName||'Magazyn')+
    ' · '+p.items.length+' szt. · '+hEsc(new Date(p.createdAt).toLocaleString('pl-PL'))+
    '</small><small>'+hEsc(p.status==='PendingIntermediateDelivery'?'U kierownika - czeka na przekazanie':p.status)+'</small></div>'+
    '<button type="button" class="secondary" data-h-print="'+p.id+'">Drukuj / PDF</button></div>').join(''):
    '<p class="meta">Nie utworzono jeszcze protokołów.</p>';
}
function hRenderItems(){
  const area=$('handoverItems');if(!area)return;
  area.innerHTML=handover.assets.length?handover.assets.map(a=>
    '<label><input type="checkbox" data-h-asset="'+hEsc(a.id)+'" '+(handover.selected.has(a.id)?'checked':'')+'>'+
    '<span><strong>'+hEsc(a.name)+'</strong><small>'+hEsc(a.key)+' · '+hEsc(a.type)+
    (a.owner?' · '+hEsc(a.owner):' · Magazyn / bez właściciela')+'</small></span></label>').join(''):
    '<div class="empty">Wybierz pracownika lub wyszukaj sprzęt po nazwie/ID.</div>';
  $('handoverCount').textContent='Wybrano: '+handover.selected.size+' / 20';
  $('handoverSubmit').disabled=handover.busy||handover.selected.size===0;
}
function hRender(){
  if(!$('handoverApp'))return;
  $('handoverApp').innerHTML=`
    <div class="toolbar"><h2>Protokół zdawczo-odbiorczy</h2>
      <span class="meta">Dostęp kontrolowany przez grupę AD</span></div>
    <div class="handover-tabs">
      <button class="secondary" type="button" data-h-mode="return" aria-selected="${handover.mode==='return'}">Zdanie sprzętu do magazynu</button>
      <button class="secondary" type="button" data-h-mode="transfer" aria-selected="${handover.mode==='transfer'}">Przekazanie sprzętu pracownikowi</button>
    </div>
    <div class="handover-layout">
      <div class="settingsCard">
        <h2>1. Pracownicy</h2>
        <div class="handover-field">
          <label for="hSourceSearch">Pracownik zdający ${handover.mode==='return'?'(wymagane)':'(opcjonalnie)'}</label>
          <input id="hSourceSearch" autocomplete="off" placeholder="Szukaj w AD..." value="${hEsc(handover.queries.source)}">
          <div class="handover-results" id="hSourceResults"></div>
          <div id="hSourceChosen">${hPerson('source')}</div>
        </div>
        ${handover.mode==='transfer'? `
        <div class="handover-field">
          <label for="hTargetSearch">Pracownik odbierający (wymagane)</label>
          <input id="hTargetSearch" autocomplete="off" placeholder="Szukaj w AD..." value="${hEsc(handover.queries.target)}">
          <div class="handover-results" id="hTargetResults"></div>
          <div id="hTargetChosen">${hPerson('target')}</div>
        </div>`:''}
        ${handover.mode==='transfer'? `
        <div class="handover-field">
          <label for="hIntermediateSearch">Pośrednik / kierownik (opcjonalnie)</label>
          <input id="hIntermediateSearch" autocomplete="off" placeholder="Wyszukaj kierownika, który odbierze sprzęt" value="${hEsc(handover.queries.intermediate)}">
          <div class="handover-results" id="hIntermediateResults"></div>
          <div id="hIntermediateChosen">${hPerson('intermediate')}</div>
        </div>`:''}
        <div class="handover-note">Jeśli wybierzesz pośrednika, musi on odebrać sprzęt i osobno wydać go finalnemu użytkownikowi. Do momentu odbioru końcowego przekazanie pozostaje nierozliczone.</div>
        <div class="handover-field">
          <label for="hEvidenceMode">Metoda potwierdzenia</label>
          <select id="hEvidenceMode">
            <option value="hybrid" ${handover.evidenceMode==='hybrid'?'selected':''}>Elektroniczna lub papierowa</option>
            <option value="electronic" ${handover.evidenceMode==='electronic'?'selected':''}>Elektroniczna (Windows SSO)</option>
            <option value="paper" ${handover.evidenceMode==='paper'?'selected':''}>Papierowa / podpisany protokol</option>
          </select>
        </div>
      </div>
      <div class="settingsCard">
        <h2>2. Wybierz sprzęt</h2>
        <div class="handover-field">
          <label for="hAssetSearch">Nazwa, numer seryjny lub ID Jira</label>
          <div style="display:flex;gap:8px">
            <input id="hAssetSearch" style="flex:1;min-width:0" autocomplete="off" placeholder="np. laptop / AST-1042" value="${hEsc(handover.queries.asset)}">
            <button class="secondary" type="button" data-h-action="search">Szukaj</button>
          </div>
        </div>
        <p class="meta">${handover.source?'Sprzęt przypisany do wybranej osoby.':'Wyszukaj katalog Jira (co najmniej 2 znaki).'}</p>
        <div id="handoverItems" class="handover-items"></div>
        <p id="handoverCount" class="handover-counter"></p>
        <label>Uwagi do protokołu<textarea id="handoverNotes" rows="3" maxlength="2000" placeholder="Stan sprzętu, wyposażenie, informacje dodatkowe...">${hEsc(handover.queries.notes)}</textarea></label>
        <div class="handover-note">Protokół do podpisu. Wygenerowanie dokumentu nie zmienia jeszcze właściciela w Jira.</div>
        <p id="handoverStatus" role="status" aria-live="polite" hidden></p>
        <div class="actions"><button type="button" class="primary" id="handoverSubmit" disabled>Utwórz protokół</button></div>
      </div>
    </div>
    <div class="settingsCard handover-history" style="max-width:none">
      <div class="toolbar"><h2>3. Protokóły</h2>
        <button class="secondary" type="button" data-h-action="history">Odśwież</button></div>
      <div id="handoverHistory">Ładowanie...</div>
    </div>`;
  hRenderItems();hRenderHistory();
  if(handover.source||handover.queries.asset.length>=2)hFetchAssets();
}
async function hFetchUsers(role,q){
  const target=$('h'+(role==='source'?'Source':role==='target'?'Target':'Intermediate')+'Results');
  if(!target)return;
  if(q.trim().length<2){target.innerHTML='';return}
  target.textContent='Szukam w Active Directory...';
  try{
    const people=await api('/api/handover/users?q='+encodeURIComponent(q.trim()));
    if(!target.isConnected||handover.queries[role]!==q)return;
    handover.results[role]=people;
    target.innerHTML=people.length?people.map(p=>'<button type="button" data-h-user="'+role+'" data-h-login="'+hEsc(p.login)+'">'+
      '<strong>'+hEsc(p.name)+'</strong><small>'+hEsc(p.login)+' · '+hEsc(p.department||p.email)+'</small></button>').join(''):
      '<p style="padding:10px">Brak wyników w AD.</p>';
  }catch(e){target.textContent=e.message}
}
async function hFetchAssets(){
  if(!me?.handover||$('handoverView').hidden)return;
  const query=handover.queries.asset.trim();
  if(!handover.source&&query.length<2){
    handover.assets=[];hRenderItems();hStatus('Wpisz co najmniej 2 znaki lub wybierz pracownika.');return;
  }
  const seq=++handover.request;
  hStatus('Pobieram sprzęt z Jira Assets...');
  try{
    const params=new URLSearchParams();
    if(query)params.set('q',query);
    if(handover.source)params.set('owner',handover.source.login);
    const data=await api('/api/handover/assets?'+params);
    if(seq!==handover.request||$('handoverView').hidden)return;
    handover.assets=data.items||[];
    hRenderItems();
    hStatus(data.truncated?'Wyświetlono 100 wyników. Doprecyzuj wyszukiwanie.':'Znaleziono '+handover.assets.length+' urządzeń.');
  }catch(e){if(seq===handover.request){handover.assets=[];hRenderItems();hStatus(e.message,true)}}
}
async function hFetchHistory(){
  try{handover.protocols=await api('/api/handover/protocols');hRenderHistory()}
  catch(e){if($('handoverHistory'))$('handoverHistory').textContent=e.message}
}
async function hSave(){
  if(handover.busy)return;
  if(handover.mode==='return'&&!handover.source){hStatus('Wybierz osobę zdającą sprzęt.',true);return}
  if(handover.mode==='transfer'&&!handover.target){hStatus('Wybierz osobę odbierającą.',true);return}
  if(!handover.selected.size){hStatus('Zaznacz urządzenia.',true);return}
  if(!confirm('Utworzyć protokół dla '+handover.selected.size+' urządzeń? Jira nie zostanie zmieniona.'))return;
  handover.busy=true; $('handoverSubmit').disabled=true;
  try{
    const payload={mode:handover.mode,sourceLogin:handover.source?.login||'',targetLogin:handover.target?.login||'',
      intermediateLogin:handover.intermediate?.login||'',evidenceMode:handover.evidenceMode,
      assetIds:[...handover.selected.keys()],notes:$('handoverNotes').value};
    const result=await api('/api/handover/protocols',{method:'POST',body:JSON.stringify(payload)});
    handover.selected.clear();hRenderItems();await hFetchHistory();
    hStatus('Zapisano protokół ASSETS-'+result.id+'. Otwórz go do podpisu.');
    window.open(new URL('api/handover/protocols/'+result.id+'/print',appBase).href,'_blank','noopener');
  }catch(e){hStatus(e.message,true)}finally{
    handover.busy=false;
    if($('handoverSubmit'))$('handoverSubmit').disabled=!handover.selected.size;
  }
}
function loadHandover(){if(!me?.handover)return;hRender();hFetchHistory()}
document.addEventListener('click',event=>{
  if(!me?.handover||$('handoverView').hidden)return;
  const btn=event.target.closest('[data-h-mode],[data-h-user],[data-h-clear],[data-h-action],[data-h-print],#handoverSubmit');
  if(!btn||!$('handoverApp')?.contains(btn))return;
  if(btn.dataset.hMode){
    handover.mode=btn.dataset.hMode;handover.assets=[];handover.selected.clear();hRender();
  }else if(btn.dataset.hUser){
    const role=btn.dataset.hUser,p=handover.results[role].find(p=>p.login===btn.dataset.hLogin);
    if(!p)return;
    handover[role]=p;handover.queries[role]='';
    if(role==='source'){handover.assets=[];handover.selected.clear()}
    hRender();
  }else if(btn.dataset.hClear){
    handover[btn.dataset.hClear]=null;
    if(btn.dataset.hClear==='source'){handover.assets=[];handover.selected.clear()}
    hRender();
  }else if(btn.dataset.hAction==='search')hFetchAssets();
  else if(btn.dataset.hAction==='history')hFetchHistory();
  else if(btn.dataset.hPrint)window.open(new URL('api/handover/protocols/'+encodeURIComponent(btn.dataset.hPrint)+'/print',appBase).href,'_blank','noopener');
  else if(btn.id==='handoverSubmit')hSave();
});
document.addEventListener('input',e=>{
  if(!me?.handover||$('handoverView').hidden||!$('handoverApp')?.contains(e.target))return;
  const field=e.target.id,role=field==='hSourceSearch'?'source':field==='hTargetSearch'?'target':field==='hIntermediateSearch'?'intermediate':null;
  if(role){
    handover.queries[role]=e.target.value;clearTimeout(handover.timers[role]);
    const query=e.target.value;
    handover.timers[role]=setTimeout(()=>hFetchUsers(role,query),400);
  }else if(field==='hAssetSearch'){
    handover.queries.asset=e.target.value;clearTimeout(handover.timers.asset);
    handover.timers.asset=setTimeout(hFetchAssets,550);
  }else if(field==='handoverNotes'){
    handover.queries.notes=e.target.value;
  }
});
document.addEventListener('change',e=>{
  if(e.target?.id==='hEvidenceMode'){handover.evidenceMode=e.target.value;return}
  if(!me?.handover||$('handoverView').hidden||!$('handoverApp')?.contains(e.target))return;
  const id=e.target.dataset.hAsset;
  if(!id)return;
  if(e.target.checked){
    if(handover.selected.size>=20){e.target.checked=false;hStatus('Limit: 20 urządzeń na protokół.',true);return}
    const asset=handover.assets.find(a=>a.id===id);
    if(asset)handover.selected.set(id,asset);
  }else handover.selected.delete(id);
  $('handoverCount').textContent='Wybrano: '+handover.selected.size+' / 20';
  $('handoverSubmit').disabled=!handover.selected.size;
});
