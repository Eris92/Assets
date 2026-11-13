// Archived paper evidence is uploaded before manual confirmation.
// The backend computes SHA-256 from stored PDF bytes (never trust a client hash).
async function uploadEvidencePdf(file){
  if(!file){
    file=await new Promise(resolve=>{
      const chooser=document.createElement('input');
      chooser.type='file';chooser.accept='application/pdf,.pdf';
      chooser.style.display='none';document.body.appendChild(chooser);
      chooser.addEventListener('change',()=>{
        const selected=chooser.files?.[0]||null;chooser.remove();resolve(selected);
      },{once:true});
      chooser.click();
    });
  }
  if(!file)return null;
  if(file.size>10*1024*1024||file.size<5)
    throw new Error('Wybierz podpisany plik PDF do 10 MB.');
  const form=new FormData();form.append('file',file,file.name);
  const response=await fetch(new URL('api/evidence',appBase),{
    method:'POST',credentials:'same-origin',
    headers:{'X-CSRF-TOKEN':me?.csrf||''},body:form
  });
  if(!response.ok){
    let detail;try{const result=await response.json();detail=result.detail||result.error}catch{}
    throw new Error(detail||'Nie udalo sie zapisac PDF: HTTP '+response.status);
  }
  return await response.json();
}

// Individual physical custody confirmation. Never classify these actions as QES.
let custodyRows=[],custodyManagerRows=[];
const custEsc=x=>escapeHtml(x??'');
function custAllowed(row,action){
 const principal=(me?.name||'').toLowerCase();
 const match=value=>value?.toLowerCase()===principal||(value==='WAREHOUSE'&&me?.handover);
 if(action==='dispatch')return row.status==='PendingDispatch'&&match(row.sender)&&row.evidenceMode!=='paper';
 if(action==='receive')return row.status==='PendingReceipt'&&match(row.receiver)&&row.evidenceMode!=='paper';
 if(action==='manual')return me?.handover&&row.evidenceMode!=='electronic'&&
   ['PendingDispatch','PendingReceipt'].includes(row.status);
 if(action==='decline')return ['PendingDispatch','PendingReceipt'].includes(row.status)&&
   (match(row.sender)||match(row.receiver));
 return false;
}
function renderCustodyInbox(){
 const area=$('custodyInboxRows');if(!area)return;
 area.innerHTML=custodyRows.length?custodyRows.map(row=>{
   const items=(row.items||[]).map(x=>'<li>'+custEsc(x.key)+' · '+custEsc(x.name)+'</li>').join('');
   const role=row.status==='PendingDispatch'?'Wydanie':'Odbior';
   return '<article class="row" style="grid-template-columns:1fr"><div><h3>Protokol ASSETS-'+row.protocolId+
     ' / etap '+row.stage+' <span class="badge">'+custEsc(row.status)+'</span></h3>'+
     '<p>Od: <strong>'+custEsc(row.sender)+'</strong> · Do: <strong>'+custEsc(row.receiver)+'</strong></p>'+
     '<p class="meta">'+row.assetCount+' urzadzen · '+custEsc(row.createdAt)+' · Tryb: '+custEsc(row.evidenceMode)+'</p>'+
     '<details><summary>Pokaz sprzet, ktory potwierdzasz</summary><ul>'+items+'</ul></details>'+
     '</div><div class="custodyAction">'+
     '<a class="secondary" target="_blank" rel="noopener" href="'+
       new URL('api/handover/protocols/'+row.protocolId+'/print',appBase).href+'">Otworz caly protokol</a>'+
     (custAllowed(row,'dispatch')?'<button class="primary" data-cust-action="dispatch" data-cust-id="'+row.protocolId+'" data-cust-stage="'+row.stage+'">Potwierdz wydanie</button>':'')+
     (custAllowed(row,'receive')?'<button class="primary" data-cust-action="receive" data-cust-id="'+row.protocolId+'" data-cust-stage="'+row.stage+'">Potwierdz odbior</button>':'')+
     (custAllowed(row,'manual')?'<button class="secondary" data-cust-action="manual" data-cust-id="'+row.protocolId+'" data-cust-stage="'+row.stage+'">Zarejestruj podpis papierowy</button>':'')+
     (custAllowed(row,'decline')?'<button class="secondary" data-cust-action="decline" data-cust-id="'+row.protocolId+'" data-cust-stage="'+row.stage+'">Odmow</button>':'')+
     '<a class="secondary" target="_blank" rel="noopener" href="'+
       new URL('api/handover/protocols/'+row.protocolId+'/evidence',appBase).href+'">Dowody JSON</a>'+
     '</div></article>';
 }).join(''):'<p class="empty">Brak operacji wymagajacych potwierdzenia.</p>';
 const pending=$('pendingManagerAssets');
 if(!pending)return;
 pending.innerHTML=custodyManagerRows.length?custodyManagerRows.map(p=>{
   const age=Math.max(0,Math.floor((Date.now()-new Date(p.receivedAt).getTime())/86400000));
   return '<div class="protocol-row"><div><strong>ASSETS-'+p.id+'</strong> · '+p.items.length+' urzadzen'+
    '<small>Odpowiedzialny: '+custEsc(p.manager)+'</small>'+
    '<small>Od '+age+' dni czeka na przekazanie odbiorcy.</small><small>'+
    p.items.map(x=>custEsc(x.key)).join(', ')+'</small></div>'+
    '<span class="badge '+(age>=7?'Rejected':'')+'">U kierownika</span></div>';
 }).join(''):'<p class="meta">Nie masz nierozliczonych przekazan posrednich.</p>';
}
async function loadCustodyInbox(silent=false){
 try{
  const [inbox,pending]=await Promise.all([
    api('/api/handover/inbox'),api('/api/handover/pending-custody')
  ]);
  custodyRows=inbox||[];custodyManagerRows=pending||[];
  const badge=$('custodyPendingIndicator');
  if(badge)badge.textContent=custodyRows.length?' ('+custodyRows.length+')':'';
  if(!silent||!$('custodyInboxView').hidden)renderCustodyInbox();
 }catch(err){
  if(!silent&&$('custodyInboxRows'))$('custodyInboxRows').textContent=err.message;
 }
}
$('custodyRefresh').onclick=()=>loadCustodyInbox();
document.addEventListener('click',async event=>{
 const btn=event.target.closest('[data-cust-action]');
 if(!btn)return;
 const action=btn.dataset.custAction,protocolId=Number(btn.dataset.custId),
   stage=Number(btn.dataset.custStage);
 const row=custodyRows.find(x=>x.protocolId===protocolId&&x.stage===stage);
 if(!row||!custAllowed(row,action))return;
 let evidenceSha256='';
 if(action==='manual'){
   try {
     const uploaded=await uploadEvidencePdf();
     if(!uploaded)return;
     evidenceSha256=uploaded.sha256;
   } catch(err){message(err.message,true);return}
 }
 const wording=action==='receive'?'Potwierdzam fizyczne odebranie wymienionych urzadzen':
   action==='dispatch'?'Potwierdzam fizyczne wydanie wymienionych urzadzen':
   action==='manual'?'Potwierdzam, ze posiadam podpisany protokol papierowy o wskazanym hashu':
   'Odmawiam potwierdzenia tej operacji';
 if(!confirm(wording+'. To oswiadczenie zapisze moja tozsamosc Windows, date i dowod audytowy. Kontynuowac?'))return;
 btn.disabled=true;
 try{
  await api('/api/handover/protocols/'+protocolId+'/stage/'+stage,{
    method:'POST',body:JSON.stringify({action,confirmed:true,evidenceSha256})
  });
  message('Oswiadczenie zapisane.');await loadCustodyInbox();
  if(me?.handover&&typeof hFetchHistory==='function')await hFetchHistory();
 }catch(err){message(err.message,true)}
 finally{btn.disabled=false}
});
