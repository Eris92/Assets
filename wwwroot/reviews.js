// Annual inventory and rights attestations. Plain Windows SSO acknowledgements
// are evidence events, not qualified electronic signatures.
let myReviewRows=[];
function renderAnnualReviews() {
 const container=$('annualReviewRows'); if(!container)return;
 if(!myReviewRows.length){container.innerHTML='<p class="empty">Brak przegladow do wyswietlenia.</p>';return}
 const year=new Date().getUTCFullYear();
 const list=myReviewRows.filter(x=>x.year===year);
 container.innerHTML=list.map(row=>{
   const done=row.status==='Confirmed';
   const name=row.kind==='assets'?'Spis z natury - moj sprzet':'Przeglad moich uprawnien';
   const days=Math.ceil((new Date(row.dueOn+'T23:59:59Z').getTime()-Date.now())/86400000);
   const status=done?'Potwierdzony':row.status==='Discrepancy'?'Zgloszono niezgodnosc':'Oczekuje';
   return '<article class="row"><div><h3>'+escapeHtml(name)+'</h3>'+
     '<p>Termin: <strong>'+escapeHtml(row.dueOn)+'</strong> · '+(done?'Zakonczony':days<0?'Po terminie':days+' dni pozostalo')+'</p>'+
     '<p class="meta">Status: '+status+(done?' · '+escapeHtml(row.mode)+' · '+escapeHtml(row.completedAt):'')+'</p>'+
     (done?'<p class="meta">SHA-256 snapshot: '+escapeHtml(row.snapshotSha256)+'</p>':'')+
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
document.addEventListener('click',async e=>{
 const button=e.target.closest('[data-review-confirm],[data-review-discrepancy]');
 if(!button)return;
 const kind=button.dataset.reviewConfirm||button.dataset.reviewDiscrepancy;
 const discrepancy=Boolean(button.dataset.reviewDiscrepancy);
 const text=kind==='assets'?'potwierdzam sprawdzenie faktycznie posiadanego sprzetu':
   'potwierdzam dokonanie przegladu wyswietlonych uprawnien AD';
 if(!confirm(discrepancy?'Zglosic niezgodnosc? Przeglad nadal pozostanie wymagany.':
    'Swiadomie oswiadczam, ze '+text+'. Moja tozsamosc Windows i czas zostana zarejestrowane.'))return;
 button.disabled=true;
 try {
  await api('/api/reviews/'+kind+'/confirm',{method:'POST',body:JSON.stringify({confirmed:true,discrepancy})});
  await loadAnnualReviews();
  message(discrepancy?'Zgloszono niezgodnosc. Przeglad pozostaje otwarty.':'Potwierdzenie zapisano w audycie.');
 }catch(error){message(error.message,true)}
 finally{button.disabled=false}
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
