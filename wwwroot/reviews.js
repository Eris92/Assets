// Annual inventory and rights attestations. Plain Windows SSO acknowledgements
// are evidence events, not qualified electronic signatures.
let myReviewRows=[];
function renderAnnualReviews() {
 const container=$('annualReviewRows'); if(!container)return;
 if(!myReviewRows.length){container.innerHTML='<p class="empty">Brak przegladow do wyswietlenia.</p>';return}
 const year=new Date().getUTCFullYear();
 const manual=$('manualReviewPanel');
 if(manual){manual.hidden=!me?.admin;$('manualReviewYear').value=year}
 const list=myReviewRows.filter(x=>x.year===year);
 container.innerHTML=list.map(row=>{
   const done=row.status==='Confirmed';
   const name=row.kind==='assets'?'Spis z natury - moj sprzet':'Przeglad moich uprawnien';
   const days=Math.ceil((new Date(row.dueOn+'T23:59:59Z').getTime()-Date.now())/86400000);
   const status=done?'Potwierdzony':row.status==='Discrepancy'?'Zgloszono niezgodnosc':'Oczekuje';
   return '<article class="row"><div><h3>'+escapeHtml(name)+'</h3>'+
     '<p>Termin: <strong>'+escapeHtml(row.dueOn)+'</strong> · '+(done?'Zakonczony':days<0?'Po terminie':days+' dni pozostalo')+'</p>'+
     '<p class="meta">Status: '+status+(done?' · '+escapeHtml(row.mode)+' · '+escapeHtml(row.completedAt):'')+'</p>'+
     (done?'<p class="meta">SHA-256 snapshot: '+escapeHtml(row.snapshotSha256)+
     ' · <a href="'+new URL('api/reviews/'+row.kind+'/'+row.year+'/evidence',appBase).href+
     '" target="_blank" rel="noopener">Dane potwierdzenia</a></p>':'')+
     '</div>'+(done?'<span class="badge Approved">Wykonano</span>':
     '<div class="decision"><button data-review-confirm="'+row.kind+'">Potwierdz przeglad</button>'+
     '<button class="reject" data-review-discrepancy="'+row.kind+'">Zglos niezgodnosc</button></div>')+'</article>';
 }).join('');
}
async function loadAnnualReviews(){
 try {
  myReviewRows=await api('/api/reviews/mine');
  renderAnnualReviews();
  const active=myReviewRows.filter(x=>x.year===new Date().getUTCFullYear()&&x.status!=='Confirmed');
  const pending=$('reviewPendingIndicator');
  if(pending)pending.textContent=active.length?' ('+active.length+')':'';
 } catch(error){if($('annualReviewRows'))$('annualReviewRows').textContent=error.message}
}
let reviewCurrent=null;
async function openReviewSnapshot(kind,discrepancy){
 let snapshot;
 try{snapshot=await api('/api/reviews/'+kind+'/snapshot')}
 catch(error){message(error.message,true);return}
 reviewCurrent={kind,discrepancy,snapshot};
 let dlg=$('reviewSnapshotDialog');
 if(!dlg){
  dlg=document.createElement('dialog');dlg.id='reviewSnapshotDialog';
  document.body.appendChild(dlg);
 }
 const items=Array.isArray(snapshot.items)?snapshot.items:[];
 const list=items.map((x,i)=>{
   const text=kind==='assets' ? [x.key,x.name,x.type,x.serial].filter(Boolean).join(' · ') : String(x);
   return '<label style="display:flex;align-items:center;gap:8px;margin:7px 0;font-size:13px">'+
     '<input data-review-item="'+i+'" type="checkbox" style="width:18px;flex:0 0 18px">'+
     '<span>'+escapeHtml(text)+'</span></label>';
 }).join('');
 dlg.innerHTML='<h2>Przeglad '+(kind==='assets'?'sprzetu':'uprawnien AD')+'</h2>'+
   '<p>Źródlo: '+escapeHtml(snapshot.source)+' · Elementy: '+items.length+'</p>'+
   '<p class="meta">Zaznacz kazda pozycje, ktora fizycznie lub merytorycznie zweryfikowales.'+
   ' Potwierdzenie nie jest kwalifikowanym podpisem elektronicznym.</p>'+
   '<div style="max-height:45vh;overflow:auto;border:1px solid #dde6eb;padding:12px;border-radius:8px">'+
   (list||'<p>Brak elementow w zrodle danych.</p>')+'</div>'+
   '<label style="display:flex;align-items:center;gap:8px;margin-top:12px">'+
   '<input id="reviewDeclaration" type="checkbox" style="width:18px"> '+
   (discrepancy?'Zglaszam niezgodnosc i wskazuje, ze przeglad nie zostal zakonczony.':
   'Swiadomie potwierdzam, ze sprawdzilem wszystkie wyswietlone pozycje i dane sa zgodne.')+
   '</label>'+
   '<div class="actions"><button id="reviewCancel" class="secondary">Anuluj</button>'+
   '<button id="reviewAccept" class="primary" disabled>'+(discrepancy?'Zglos niezgodnosc':'Potwierdz przeglad')+'</button></div>';
 const eligible=()=>{
   const checks=Array.from(dlg.querySelectorAll('[data-review-item]'));
   return $('reviewDeclaration').checked&&(discrepancy||checks.every(x=>x.checked));
 };
 dlg.addEventListener('change',()=>{
   const button=$('reviewAccept');if(button)button.disabled=!eligible();
 },{signal:undefined});
 $('reviewCancel').onclick=()=>dlg.close();
 $('reviewAccept').onclick=async()=>{
   if(!eligible()||!reviewCurrent)return;
   const btn=$('reviewAccept');btn.disabled=true;
   try{
     await api('/api/reviews/'+kind+'/confirm',{method:'POST',
       body:JSON.stringify({confirmed:true,discrepancy,expectedHash:snapshot.sha256})});
     dlg.close();await loadAnnualReviews();
     message(discrepancy?'Niezgodnosc zapisana. Przeglad pozostaje otwarty.':
       'Potwierdzenie zapisano wraz ze snapshotem i SHA-256.');
   }catch(e){message(e.message,true);btn.disabled=false}
 };
 dlg.showModal();
}
document.addEventListener('click',e=>{
 const button=e.target.closest('[data-review-confirm],[data-review-discrepancy]');
 if(!button)return;
 openReviewSnapshot(button.dataset.reviewConfirm||button.dataset.reviewDiscrepancy,
   Boolean(button.dataset.reviewDiscrepancy));
});
async function loadTeamReviewStatus(){
 if(!me?.manager)return;
 const target=$('teamReviewRows');if(!target)return;
 target.textContent='Pobieranie...';
 try{
  const data=await api('/api/team/reviews');
  const year=new Date().getUTCFullYear();
  const rows=data.flatMap(p=>(p.reviews||[]).filter(r=>r.year===year).map(r=>({
    user:p.Name,login:p.Login,...r
  })));
  target.innerHTML='<div class="equipmentTableWrap"><table class="equipmentTable">'+
    '<thead><tr><th>Pracownik</th><th>Przeglad</th><th>Termin</th><th>Status</th></tr></thead><tbody>'+
    rows.map(r=>'<tr><td>'+escapeHtml(r.user)+'<small>'+escapeHtml(r.login)+'</small></td>'+
      '<td>'+(r.kind==='assets'?'Sprzet':'Uprawnienia')+'</td><td>'+escapeHtml(r.dueOn)+'</td>'+
      '<td>'+escapeHtml(r.status)+'</td></tr>').join('')+'</tbody></table></div>';
 }catch(error){target.textContent=error.message}
}
$('teamReviewRefresh').onclick=loadTeamReviewStatus;
$('annualReviewRefresh').onclick=loadAnnualReviews;

async function submitManualAnnualReview(e){
 e.preventDefault();if(!me?.admin)return;
 const file=$('manualReviewPdf').files?.[0];
 if(!file){message('Wybierz podpisany PDF.',true);return}
 const login=$('manualReviewLogin').value.trim();
 const year=Number($('manualReviewYear').value);
 const kind=$('manualReviewKind').value;
 if(!confirm('Potwierdzic przeglad na podstawie podpisanego dokumentu? Ten zapis zarchiwizuje PDF i SHA-256.'))return;
 const button=$('manualReviewSubmit');button.disabled=true;
 try{
   const uploaded=await uploadEvidencePdf(file);
   if(!uploaded)return;
   const body={login,kind,year,sha256:uploaded.sha256,
     evidenceReference:'AssetsEvidenceVault/'+uploaded.sha256};
   await api('/api/admin/reviews/manual',{method:'POST',body:JSON.stringify(body)});
   message('Zapisano papierowy przeglad i archiwum PDF SHA-256: '+uploaded.sha256);
   $('manualReviewForm').reset();await loadAnnualReviews();
 }catch(err){message(err.message,true)}
 finally{button.disabled=false}
}
$('manualReviewForm').addEventListener('submit',submitManualAnnualReview);
