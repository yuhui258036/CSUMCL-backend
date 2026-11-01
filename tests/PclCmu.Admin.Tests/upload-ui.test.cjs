const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const crypto=require('node:crypto');
const source=fs.readFileSync(path.join(__dirname,'../../upload.js'),'utf8');
const workerSource=fs.readFileSync(path.join(__dirname,'../../sha256-worker.js'),'utf8');
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
let checks=0;
function check(condition,name){assert.ok(condition,name);checks++;console.log('ok: '+name);}

function browser(server,storage,file,options={}){
  const elements=new Map();
  function element(id){
    if(!elements.has(id))elements.set(id,{hidden:true,disabled:false,textContent:'',value:0,events:{},dataset:{},removeAttribute(name){delete this[name];},
      classList:{add(){},remove(){}}, click(){this.clicked=true;},
      dispatchEvent(event){this.events[event.type]?.(event);},
      addEventListener(name,handler){this.events[name]=handler;}});
    return elements.get(id);
  }
  const form=element('upload-form');form.dataset={instance:'test',revision:'0',chunkSize:'65536'};form.elements={_csrf:{value:'test-csrf'}};
  element('upload-file').files=[file];
  class Worker {
    constructor(){
      const self={postMessage:data=>{if(!this.dead)this.onmessage?.({data});}};
      this.context=vm.createContext({self,Uint8Array,Uint32Array,DataView,crypto:crypto.webcrypto});
      vm.runInContext(workerSource,this.context);
    }
    postMessage(data){setImmediate(()=>{if(!this.dead)this.context.self.onmessage({data});});}
    terminate(){this.dead=true;}
  }
  class XHR {
    constructor(){this.upload={};this.headers={};this.status=0;}
    open(method,url){this.url=url;}
    setRequestHeader(name,value){this.headers[name]=value;}
    send(blob){
      this.index=Number(this.url.split('/').pop());
      server.active++;server.maximum=Math.max(server.maximum,server.active);
      if (options.partialPause) {
        this.upload.onprogress?.({loaded:blob.size / 2});
        options.partialPause();
        if (this.ended) return;
      }
      this.timer=setTimeout(async()=>{
        if(this.ended)return;
        const bytes=Buffer.from(await blob.arrayBuffer());
        const hash=crypto.createHash('sha256').update(bytes).digest('hex');
        assert.equal(hash,this.headers['X-Chunk-SHA256']);
        assert.equal(this.headers.Authorization,'Bearer test-grant');
        this.upload.onprogress?.({loaded:bytes.length});
        server.received.add(this.index);server.sends.push(this.index);
        this.ended=true;server.active--;
        if(options.responseLoss&&!server.lost){server.lost=true;this.onerror?.();}
        else{this.status=204;this.onload?.();}
        options.afterAck?.(this.index);
      },options.slow?25:1);
    }
    abort(){if(this.ended)return;this.ended=true;clearTimeout(this.timer);server.active--;this.onabort?.();}
  }
  function status(){return {...server.task,received:[...server.received]};}
  async function fetch(url,request){
    assert.equal(request.headers['X-CSRF-Token'],'test-csrf');
    const method=request.method;
    let value;
    if(url==='/admin/api/uploads'&&method==='POST'){
      const body=JSON.parse(request.body);
      assert.equal(body.sha256,server.hash);
      if(!server.task || ['completed','cancelled'].includes(server.task.state)) {server.received.clear(); server.task={id:'session',instanceId:'test',fileName:file.name,size:file.size,sha256:body.sha256,baseRevision:body.baseRevision,
        chunkSize:65536,chunkCount:Math.ceil(file.size/65536),state:'receiving',chunkBaseUrl:'https://cdn.example.com/api/uploads/session/chunks/'};}
      if(options.afterCreate) await options.afterCreate();
      value=status();
    }else if(url.endsWith('/renew'))value={token:'test-grant',expiresUtc:new Date(Date.now()+3600000).toISOString()};
    else if(url.endsWith('/complete')){
      assert.equal(server.received.size,server.task.chunkCount);
      server.task.state=options.delayedPublish?'queued':'completed';server.task.revision=server.task.baseRevision+1;value=status();server.publishes++;
    }else if(method==='GET'){if(server.task.state==='queued'&&server.release)server.task.state='completed';value=status();}
    else if(method==='DELETE'){
      assert.equal(server.active,0,'cancel must settle all active XHRs before deleting');
      server.deletes=(server.deletes||0)+1;
      if(options.cancelBusyOnce&&server.deletes===1)return {ok:false,status:409,json:async()=>({error:'Wait for active requests/verification to finish before cancelling.'})};
      server.task.state='cancelled';server.received.clear();value=null;
    }
    else throw new Error('Unexpected API '+method+' '+url);
    return {ok:true,status:value===null?204:200,json:async()=>value};
  }
  let controller;
  const root={querySelector:selector=>selector==='h2'?{textContent:'Test'}:element(selector.slice(1))};
  const runtime=options.runtime?{get(){},changed(){},register(id,value){controller=value;},getFile:options.runtime.getFile||(()=>Promise.resolve(null)),
    putFile:options.runtime.putFile||(()=>Promise.resolve()),removeFile(){}}:null;
  const context=vm.createContext({document:{getElementById:element,querySelector:()=>root,addEventListener(){}},
    ...(runtime?{window:{CmuUploads:runtime,addEventListener(){}},File,DataTransfer:class{constructor(){throw new Error('Native FileList unavailable');}}}:{}),
    localStorage:{getItem:k=>storage.get(k),setItem:(k,v)=>storage.set(k,v),removeItem:k=>storage.delete(k)},
    Worker,XMLHttpRequest:XHR,fetch,performance,DOMException,Event,URL,console,Math,Date,
    setTimeout:(callback,ms)=>setTimeout(callback,Math.min(ms,5)),clearTimeout});
  vm.runInContext(source,context);
  return {element,attach:()=>controller.attach(),submit:()=>form.events.submit({preventDefault(){}}),pause:()=>element('upload-pause').events.click()};
}
async function main(){
  const bytes=crypto.randomBytes(240000),file=new Blob([bytes]);file.name='test.mrpack';
  const makeServer=()=>({hash:crypto.createHash('sha256').update(bytes).digest('hex'),received:new Set(),sends:[],active:0,maximum:0,publishes:0});
  {
    const server=makeServer(),page=browser(server,new Map(),undefined);
    await page.submit();
    check(!page.element('upload-error').hidden&&page.element('upload-error').textContent.includes('请选择要上传')&&!server.task,
      'missing file uses inline Chinese validation without creating a task');
    check(page.element('upload-form').noValidate&&!page.element('upload-file').required,'upload bypasses native validation bubbles');
  }
  {
    let resolveRestore;const writes=[],server=makeServer();
    const page=browser(server,new Map(),file,{runtime:{getFile:()=>new Promise(resolve=>resolveRestore=resolve),putFile:async(id,file)=>writes.push(file)}});
    page.element('upload-file').events.change();
    const stale=new File(['old'],'old.mrpack');resolveRestore({file:stale,name:stale.name,modified:0});
    await delay(5);
    page.element('upload-file').files=[];page.attach();
    check(page.element('selected-file-name').textContent===file.name&&writes[0]===file,
      'late cache restore cannot overwrite a newer file selection');
    page.element('upload-file').events.change();
    check(page.element('selected-file-name').textContent===file.name,'empty native selection preserves retained file');
    await page.submit();
    check(server.publishes===1,'reattached form uploads retained file even when native FileList is empty');
  }
  {
    const server=makeServer(),page=browser(server,new Map(),file),zone=page.element('upload-dropzone');
    const drop=files=>zone.events.drop({preventDefault(){},dataTransfer:{files}});
    page.element('upload-file').files=[];
    drop([file]);
    check(page.element('upload-file').files[0]===file&&page.element('selected-file-name').textContent===file.name&&server.publishes===0,
      'dropping a valid file selects it without starting upload');
    const invalid=new Blob(['invalid']);invalid.name='wrong.zip';
    drop([invalid]);
    check(page.element('upload-file').files[0]===file&&!page.element('upload-error').hidden,'invalid drop preserves selected pack');
    drop([file,file]);
    check(page.element('upload-error').textContent.includes('一个'),'multiple files are rejected');
    page.element('upload-file').disabled=true;
    drop([invalid]);
    check(page.element('upload-file').files[0]===file,'dropping during upload cannot replace the active file');
    page.element('upload-file').disabled=false;
    zone.events.keydown({target:zone,key:'Enter',preventDefault(){}});
    check(page.element('upload-file').clicked,'drop area supports keyboard file selection');
  }
  {
    const storage=new Map(),server=makeServer();
    const page=browser(server,storage,file,{responseLoss:true});
    await page.submit();
    check(server.publishes===1,'browser workflow publishes exactly once after a lost chunk response');
    check(server.sends.length===server.task.chunkCount+1,'only the failed-response chunk is retried');
    check(server.maximum===2,'browser upload has bounded two-lane concurrency');
    check(!page.element('upload-result').hidden&&!storage.size,'completed UI shows success and clears resume pointer');
    check(page.element('upload-form').dataset.revision==='1','successful publish advances the page base revision');
    await page.submit();
    check(server.publishes===2&&server.task.revision===2,'same page can publish a second revision');
    server.task.state='failed';server.task.error='Full file SHA-256 does not match.';
    storage.set('pcl-upload:test',JSON.stringify({id:'session'}));
    const failed=browser(server,storage,file);await delay(10);
    check(failed.element('upload-submit').disabled&&!failed.element('upload-cancel').disabled,'failed publication asks for cancellation instead of unsupported retry');
    check(failed.element('upload-error').textContent.includes('校验不一致'),'server hash error is presented in Chinese');
    failed.element('upload-cancel').events.click();
    check(!failed.element('upload-cancel-confirm').hidden,'cancelling a task requires explicit UI confirmation');
    await failed.element('upload-cancel-yes').events.click();
    check(!storage.size&&!failed.element('upload-submit').disabled,'cancelled task clears resume state and permits a new upload');
  }
  {
    const storage=new Map(),server=makeServer();let page,shown;
    page=browser(server,storage,file,{partialPause:()=>{shown=page.element('upload-progress').value;page.pause();}});
    await page.submit();
    check(shown>0&&page.element('upload-progress').value===shown&&server.received.size===0,
      'pause retains in-flight visual progress before any chunk is confirmed');
    check(page.element('upload-stage').textContent==='已暂停。','pause status contains only the requested text');
  }
  {
    const storage=new Map(),server=makeServer();let paused=false,page;
    page=browser(server,storage,file,{slow:true,afterAck:()=>{if(!paused){paused=true;page.pause();}}});
    await page.submit();
    check(server.publishes===0&&server.received.size>0&&server.received.size<4,'pause retains confirmed chunks without publishing');
    check(!page.element('upload-submit').disabled&&storage.size===1,'paused UI can be resumed after reload');
    const acknowledged=[...server.received];server.sends=[];
    const changed=new Blob([crypto.randomBytes(bytes.length)]);changed.name='different.mrpack';
    const mismatch=browser(server,storage,changed);await delay(10);await mismatch.submit();
    check(mismatch.element('upload-error').textContent.includes('所选文件与未完成的上传不同')&&server.sends.length===0,'resume rejects another file without overwriting or sending blocks');
    const resumed=browser(server,storage,file);
    await delay(10);await resumed.submit();
    check(server.publishes===1,'reloaded page completes original upload');
    check(acknowledged.every(i=>!server.sends.includes(i)),'reloaded page skips all previously acknowledged chunks');
  }
  {
    const storage=new Map(),server=makeServer();let page,cancelled;
    page=browser(server,storage,file,{slow:true,cancelBusyOnce:true,partialPause:()=>{
      if(cancelled)return;
      check(!page.element('upload-cancel').disabled,'cancel is enabled during active transfer');
      page.element('upload-cancel').events.click();
      check(!page.element('upload-cancel-confirm').hidden,'active upload can open cancellation confirmation');
      cancelled=page.element('upload-cancel-yes').events.click();
    }});
    await page.submit();await cancelled;
    check(server.task.state==='cancelled'&&server.active===0&&server.publishes===0&&server.deletes===2,
      'active cancel settles both lanes and retries a busy server without publishing');
    check(page.element('upload-file').files[0]===file&&!page.element('upload-submit').disabled,
      'cancel retains file selection and allows another upload');
    await page.submit();
    check(server.publishes===1,'retained file can be uploaded again after cancellation');
  }
  {
    const server=makeServer(),page=browser(server,new Map(),file);
    const pending=page.submit();
    await Promise.resolve();
    const cancelled=page.element('upload-cancel-yes').events.click();
    await pending;await cancelled;
    check(!server.task&&page.element('upload-form').dataset.state==='cancelled'&&!page.element('upload-submit').disabled,
      'cancellation before hashing finishes does not create an orphan task');
  }
  {
    const server=makeServer();let page,cancelled;
    page=browser(server,new Map(),file,{afterCreate:async()=>{cancelled=page.element('upload-cancel-yes').events.click();await delay(5);}});
    await page.submit();await cancelled;
    check(server.task.state==='cancelled'&&server.sends.length===0&&server.publishes===0,
      'cancellation during session creation waits for the session then cleans it up');
  }
  {
    const storage=new Map(),server=makeServer();
    const page=browser(server,storage,file);const pending=page.submit();await Promise.resolve();page.pause();await pending;
    check(!page.element('upload-submit').disabled&&server.publishes===0,'pause while hashing settles the submit promise');
  }
  {
    const storage=new Map(),server=makeServer(),page=browser(server,storage,file,{delayedPublish:true});
    const pending=page.submit();
    for(let i=0;i<100&&page.element('upload-form').dataset.state!=='queued';i++)await delay(5);
    check(page.element('upload-pause').disabled&&page.element('upload-percent').textContent==='处理中','server publication is indeterminate and cannot be paused as a transfer');
    page.pause();server.release=true;await pending;
    check(!page.element('upload-result').hidden,'publication completes even if pause is invoked during server work');
  }
  console.log(`${checks} upload UI workflow checks passed.`);
}
main().catch(error=>{console.error(error);process.exitCode=1;});
