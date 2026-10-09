const demoUser = 'DEMO\\krzysztof';
const demoAssets = [
{id:'1',type:'Laptop',key:'IT-1042',name:'Dell Latitude 7450',attributes:[{id:'101',name:'Numer seryjny',value:'DEMO-7H42K'},{id:'102',name:'Lokalizacja',value:'Gdansk'},{id:'103',name:'Status',value:'W uzyciu'},{id:'104',name:'Nazwa sieciowa',value:'DEMO-LT042'}]},
{id:'2',type:'Monitor',key:'IT-2081',name:'Dell UltraSharp U2724D',attributes:[{id:'101',name:'Numer seryjny',value:'DEMO-MON081'},{id:'102',name:'Lokalizacja',value:'Gdansk'},{id:'103',name:'Status',value:'W uzyciu'},{id:'104',name:'Typ',value:'Monitor 27 cali'}]},
{id:'3',type:'Telefon',key:'IT-3094',name:'Apple iPhone 15',attributes:[{id:'101',name:'Numer seryjny',value:'DEMO-PHN094'},{id:'102',name:'Lokalizacja',value:'Gdansk'},{id:'103',name:'Status',value:'W uzyciu'},{id:'104',name:'Typ',value:'Telefon sluzbowy'}]}
];
let demoReports = [{id:1,name:demoAssets[0].name,key:'IT-1042',objectId:'1',attributeId:'102',attribute:'Lokalizacja',oldValue:'Gdansk',proposedValue:'Pruszcz Gdanski',reason:'Zmiana miejsca pracy.',reporter:demoUser,createdAt:'2026-10-09T10:00:00Z',source:'Jira Assets (demo)',status:'Pending'}];
let demoSettings = {siteUrl:'https://demo.atlassian.net',accountEmail:'assets@example.com',cloudId:'demo-cloud',workspaceId:'demo-workspace',aql:'objectType = "Sprzet uzytkownika"',ownerAttributeId:'105',adminGroup:'DEMO\\Assets-Portal-Admins',allowedCorrectionAttributeIds:['101','102','103','104'],tokenConfigured:false};
window.fetch = async (url, options = {}) => {
  const path = new URL(url,location.href).pathname;
  const data = options.body ? JSON.parse(options.body) : {};
  let result;
  if(path.endsWith('/api/me')) result={name:demoUser,admin:true,csrf:'demo',allowedAttributes:demoSettings.allowedCorrectionAttributeIds};
  else if(path.endsWith('/api/assets')) result=demoAssets;
  else if(path.endsWith('/api/admin/settings')) {
    if(options.method==='POST') {const {apiToken,...safe}=data;demoSettings={...demoSettings,...safe,tokenConfigured:!!apiToken||demoSettings.tokenConfigured};}
    result=demoSettings;
  } else if(path.endsWith('/api/admin/jira/workspaces')) result=[{workspaceId:'demo-workspace'}];
  else if(/\/api\/reports\/\d+\/decision$/.test(path)) {
    const id=Number(path.split('/').at(-2)); const row=demoReports.find(r=>r.id===id);
    if(!row||row.status!=='Pending') return new Response(JSON.stringify({error:'Zgloszenie zostalo juz rozpatrzone.'}),{status:409});
    row.status=data.action==='approve'?'Approved':'Rejected';
    if(row.status==='Approved') demoAssets.find(a=>a.id===row.objectId).attributes.find(a=>a.id===row.attributeId).value=row.proposedValue;
    result={};
  } else if(path.endsWith('/api/reports')) {
    if(options.method==='POST') {const asset=demoAssets.find(a=>a.id===data.objectId);const attr=asset.attributes.find(a=>a.id===data.attributeId);demoReports.push({id:demoReports.length+1,name:asset.name,key:asset.key,objectId:asset.id,attributeId:attr.id,attribute:attr.name,oldValue:attr.value,proposedValue:data.proposedValue,reason:data.reason,reporter:demoUser,createdAt:new Date().toISOString(),source:'Jira Assets (demo)',status:'Pending'});}
    result=demoReports;
  } else return new Response('{}',{status:404});
  return new Response(JSON.stringify(result),{status:200,headers:{'Content-Type':'application/json'}});
};
