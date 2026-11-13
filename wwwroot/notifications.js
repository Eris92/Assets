// Settings values are stored by the backend; secrets are never returned to this UI.
async function loadNotificationSettings(){
 try{
  const result=await api('/api/admin/notifications');
  const data=result.delivery;
  for(const[id,value]of Object.entries({
   notifyChannel:data.channel,notifyHost:data.host,notifyPort:data.port,
   notifyFrom:data.fromEmail,notifyUser:data.username,
   notifyAssetsDue:result.assetsDueMonthDay,notifyPermissionsDue:result.permissionsDueMonthDay,
   notifyEvidenceMode:result.evidenceMode
  }))$(id).value=value??'';
  $('notifyPassword').value='';$('notifyTeams').value='';
  $('notifySecretStatus').textContent='Haslo SMTP: '+(data.smtpPasswordConfigured?'zapisane':'brak')+
    ' | Webhook Teams: '+(data.teamsWebhookConfigured?'zapisany':'brak');
 }catch(e){message(e.message,true)}
}
$('notifyForm').addEventListener('submit',async e=>{
 e.preventDefault();
 const body={
   channel:$('notifyChannel').value,host:$('notifyHost').value.trim(),
   port:Number($('notifyPort').value),fromEmail:$('notifyFrom').value.trim(),
   username:$('notifyUser').value.trim(),smtpPassword:$('notifyPassword').value,
   teamsWebhook:$('notifyTeams').value.trim(),
   assetsDueMonthDay:$('notifyAssetsDue').value.trim(),
   permissionsDueMonthDay:$('notifyPermissionsDue').value.trim(),
   evidenceMode:$('notifyEvidenceMode').value
 };
 try{
  await api('/api/admin/notifications',{method:'POST',body:JSON.stringify(body)});
  await loadNotificationSettings();message('Ustawienia powiadomien zapisane.');
 }catch(ex){message(ex.message,true)}
});
$('notifyTest').onclick=async()=>{
 const channel=$('notifyTestChannel').value,to=$('notifyTestTo').value.trim();
 if(!to){message('Podaj adres email do testu.',true);return}
 try{
  await api('/api/admin/notifications/test',{method:'POST',body:JSON.stringify({channel,to})});
  message('Wiadomosc testowa wyslana.');
 }catch(ex){message(ex.message,true)}
};
