const fs = require('node:fs');
const vm = require('node:vm');
const crypto = require('node:crypto');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname,'../../sha256-worker.js'),'utf8');
const context = vm.createContext({Uint8Array,Uint32Array,DataView});
vm.runInContext(source,context);
let checks=0;
for(const size of [0,1,3,55,56,63,64,65,127,128,129,65535,65536,2097153,8388611]) {
  const bytes=crypto.randomBytes(size);
  const expected=crypto.createHash('sha256').update(bytes).digest('hex');
  for(const step of [1,17,64,65536,2097152]) {
    const hasher=new context.IncrementalSha256();
    for(let offset=0;offset<size;offset+=step)hasher.update(bytes.subarray(offset,offset+step));
    if(hasher.digest()!==expected)throw new Error(`SHA-256 mismatch size=${size} step=${step}`);
    checks++;
  }
}
// 512 MiB crosses SHA-256's low 32-bit bit-count boundary; do not allocate it.
const block=Buffer.alloc(1024*1024,0x61), hasher=new context.IncrementalSha256();
const expected=crypto.createHash('sha256');
for(let i=0;i<513;i++){hasher.update(block);expected.update(block);}
if(hasher.digest()!==expected.digest('hex'))throw new Error('SHA-256 long length carry mismatch');
console.log(`${++checks} browser streaming SHA-256 checks passed.`);
