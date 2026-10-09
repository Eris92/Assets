const teamPeople = [
{id:'anna',name:'Anna Kowalska',role:'Team Leader - Infrastructure',manager:'me'},
{id:'piotr',name:'Piotr Nowak',role:'Security Engineer',manager:'me'},
{id:'marek',name:'Marek Wisniewski',role:'System Administrator',manager:'anna'},
{id:'ewa',name:'Ewa Zielinska',role:'Helpdesk Lead',manager:'anna'},
{id:'tomasz',name:'Tomasz Wojcik',role:'IT Support',manager:'ewa'}
];
const teamEquipment = teamPeople.flatMap((person,i)=>['Laptop','Monitor','Telefon'].slice(0,i%2?2:3).map((type,j)=>({id:'team-'+i+'-'+j,key:'IT-'+(4000+i*10+j),name:type==='Laptop'?'Dell Latitude 7450':type==='Monitor'?'Dell P2725H':'Samsung Galaxy A55',owner:person.id,type,serial:'DEMO-'+i+j+'842',status:'W uzyciu',location:i<2?'Gdansk':'Pruszcz Gdanski'})));
let selectedPerson = '';
function teamDepth(person) {
  const visited=new Set(); let depth=1; let parent=person.manager;
  while(parent!=='me') { if(visited.has(parent)) return 0; visited.add(parent); const manager=teamPeople.find(p=>p.id===parent); if(!manager) return 0; parent=manager.manager; depth++; }
  return depth;
}
function loadTeam() {
 const indirect=$('includeIndirect').checked;
 const people=teamPeople.filter(p=>teamDepth(p)>0 && (indirect||p.manager==='me'));
 if(selectedPerson && !people.some(p=>p.id===selectedPerson)) selectedPerson='';
 const equipment=teamEquipment.filter(a=>people.some(p=>p.id===a.owner));
 $('teamSummary').innerHTML=[['Pracownicy',people.length],['Bezposredni',people.filter(p=>p.manager==='me').length],['Posredni',people.filter(p=>p.manager!=='me').length],['Zasoby',equipment.length]].map(([label,value])=>'<div><strong>'+value+'</strong><span>'+label+'</span></div>').join('');
 $('teamTree').innerHTML='<h3>Struktura zespolu</h3><button data-person="" class="'+(!selectedPerson?'selected':'')+'">Caly zespol</button>'+people.map(p=>'<button style="padding-left:'+ (14+(teamDepth(p)-1)*18)+'px" data-person="'+p.id+'" class="'+(selectedPerson===p.id?'selected':'')+'"><strong>'+escapeHtml(p.name)+'</strong><small>'+escapeHtml(p.role)+'</small><small>'+ (p.manager==='me'?'Bezposrednio pod Toba':'Przelozony: '+teamPeople.find(m=>m.id===p.manager).name)+'</small></button>').join('');
 $('teamSelection').textContent=selectedPerson?people.find(p=>p.id===selectedPerson).name:'Wszyscy pracownicy';
 const rows=equipment.filter(a=>!selectedPerson||a.owner===selectedPerson).map((a,index)=>{const owner=people.find(p=>p.id===a.owner);return equipmentRecord(a,index,owner.name,owner.manager==='me'?'Bezposrednia':'Posrednia - poziom '+teamDepth(owner));});
 renderEquipmentList('team','teamAssets',rows,false);
}
$('teamTree').onclick=e=>{const b=e.target.closest('[data-person]');if(b){selectedPerson=b.dataset.person;loadTeam();}};
$('includeIndirect').onchange=loadTeam;
setupEquipmentFilters('team','teamFilters',loadTeam);
$('reportType').onchange=()=>{const edit=$('reportType').value==='attribute';$('attributeFields').hidden=!edit;$('attribute').required=$('proposed').required=edit;};
function teamReportAsset(asset) {
  return {...asset,attributes:[{id:'101',name:'Numer seryjny',value:asset.serial},{id:'102',name:'Lokalizacja',value:asset.location},{id:'103',name:'Status',value:asset.status},{id:'104',name:'Typ',value:asset.type}]};
}
$('teamAssets').addEventListener('click', e => {
  const button = e.target.closest('[data-team-asset]');
  if (!button) return;
  const asset = teamEquipment.find(a=>a.id===button.dataset.teamAsset);
  const owner = asset && teamPeople.find(p=>p.id===asset.owner);
  if (owner && teamDepth(owner)>0) openEquipmentReport(teamReportAsset(asset),owner.name);
});
const originalDemoFetch=window.fetch;
window.fetch=async(url,options={})=>{
 const path=new URL(url,location.href).pathname;
 if(path.endsWith('/api/reports')&&options.method==='POST') {
  const data=JSON.parse(options.body);
  const teamAsset=teamEquipment.find(a=>a.id===data.objectId);
  if (teamAsset || data.type==='not-held') {
   const asset=teamAsset?teamReportAsset(teamAsset):demoAssets.find(a=>a.id===data.objectId);
   const owner=teamAsset && teamPeople.find(p=>p.id===teamAsset.owner);
   if(!asset || (teamAsset && (!me.admin || !owner || teamDepth(owner)<1))) return new Response('{}',{status:403});
   const allowedTypes=teamAsset?['assignment','employee-left','attribute']:['not-held'];
   if(!allowedTypes.includes(data.type)) return new Response('{}',{status:400});
   const attr=data.type==='attribute'?asset.attributes.find(a=>a.id===data.attributeId && me.allowedAttributes.includes(a.id)):null;
   if(data.type==='attribute' && !attr) return new Response('{}',{status:400});
   demoReports.push({id:Math.max(0,...demoReports.map(r=>r.id))+1,name:asset.name,key:asset.key,objectId:asset.id,type:data.type,attributeId:attr?.id,attribute:attr?.name || 'Przypisanie sprzetu',oldValue:attr?.value || owner?.name || 'Przypisany do mnie',proposedValue:attr?data.proposedValue:'Usuniecie przypisania',reason:data.reason || '',affectedEmployee:owner?.name || '',reporter:demoUser,createdAt:new Date().toISOString(),source:'Jira Assets (demo)',status:'Pending'});
   return new Response('{}',{status:200});
  }
 }
 if(/\/api\/reports\/\d+\/decision$/.test(path)) {
  const row=demoReports.find(r=>r.id===Number(path.split('/').at(-2)));
  const teamAsset=row && teamEquipment.find(a=>a.id===row.objectId);
  if(row && (row.type==='not-held' || row.objectId.startsWith('team-'))) {
   const action=JSON.parse(options.body).action;
   if(!me.admin) return new Response('{}',{status:403});
   if(!['approve','reject'].includes(action)) return new Response('{}',{status:400});
   if(row.status!=='Pending') return new Response(JSON.stringify({error:'Zgloszenie juz rozpatrzone.'}),{status:409});
   row.status=action==='approve'?'Approved':'Rejected';
   if(row.status==='Approved') {
    if(teamAsset && row.type==='attribute') {
     const field={'101':'serial','102':'location','103':'status','104':'type'}[row.attributeId];
     if(field)teamAsset[field]=row.proposedValue;
    } else {
     const collection=teamAsset?teamEquipment:demoAssets;
     const index=collection.findIndex(a=>a.id===row.objectId);
     if(index>=0)collection.splice(index,1);
    }
   }
   return new Response('{}',{status:200});
  }
 }
 return originalDemoFetch(url,options);
};
