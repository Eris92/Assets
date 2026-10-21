async function loadAccess() {
  try {
    const data=await api('/api/admin/access');
    $('accessBootstrap').textContent='Administrator awaryjny: '+(data.bootstrapGroup || '-')+(data.bootstrapUsers.length?' | '+data.bootstrapUsers.join(', '):'')+'. Konta awaryjne konfiguruje administrator serwera.';
    $('accessRows').innerHTML=data.rules.length?`<div class="equipmentTableWrap"><table class="equipmentTable"><thead><tr><th>Konto / grupa</th><th>Typ</th><th>Rola</th><th>Dostep</th><th>Akcja</th></tr></thead><tbody>${data.rules.map(r=>`<tr><td><strong>${escapeHtml(r.name)}</strong><small>${escapeHtml(r.sid)}</small></td><td>${r.kind==='Group'?'Grupa':'Uzytkownik'}</td><td>${r.role==='Admin'?'Administrator':'Uzytkownik'}</td><td>${r.effect==='Deny'?'Zablokowany':'Dozwolony'}</td><td><button class="secondary" data-access-edit="${escapeHtml(r.sid)}">Edytuj</button> <button class="secondary" data-access-remove="${escapeHtml(r.sid)}">Usun</button></td></tr>`).join('')}</tbody></table></div>`:'<p class="empty">Brak regul dostepu. Dostep maja tylko administratorzy awaryjni. Dodaj uzytkownika lub grupe AD.</p>';
    $('accessRows')._rules=data.rules;
    $('accessAudit').innerHTML=data.audit.length?data.audit.map(a=>`<p><strong>${escapeHtml(new Date(a.at).toLocaleString('pl-PL'))}</strong> | ${escapeHtml(a.actor)} | ${escapeHtml(a.action)} | ${escapeHtml(a.sid)}</p>`).join(''):'<p>Brak zmian.</p>';
  } catch(e) { message(e.message,true); }
}
$('accessForm').addEventListener('submit',async e=>{
  e.preventDefault();const button=$('accessSave');button.disabled=true;
  try {
    await api('/api/admin/access',{method:'POST',body:JSON.stringify({account:$('accessAccount').value.trim(),kind:$('accessKind').value,role:$('accessRole').value,effect:$('accessEffect').value})});
    $('accessForm').reset();await loadAccess();message('Regula dostepu zapisana.');
  } catch(err) { message(err.message,true); } finally {button.disabled=false;}
});
$('accessRows').addEventListener('click',async e=>{
  const edit=e.target.closest('[data-access-edit]');
  if(edit){const r=$('accessRows')._rules.find(r=>r.sid===edit.dataset.accessEdit);if(r){$('accessAccount').value=r.sid;$('accessKind').value=r.kind;$('accessRole').value=r.role;$('accessEffect').value=r.effect;$('accessAccount').focus();}return;}
  const button=e.target.closest('[data-access-remove]');if(!button)return;
  if(!confirm('Usunac regule? Usuniecie blokady moze przywrocic dostep nadany przez grupe.'))return;
  button.disabled=true;
  try{await api('/api/admin/access/'+encodeURIComponent(button.dataset.accessRemove),{method:'DELETE'});await loadAccess();message('Regula usunieta.');}
  catch(err){message(err.message,true);button.disabled=false;}
});
$('refreshAccess').onclick=loadAccess;
