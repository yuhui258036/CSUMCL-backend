"use strict";

// Streaming SHA-256: bounded memory for multi-gigabyte packs. This module has
// no network dependencies; tests compare it with .NET/Node SHA-256 vectors.
class IncrementalSha256 {
  constructor() {
    this.state = new Uint32Array([0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19]);
    this.pending = new Uint8Array(64); this.used = 0; this.length = 0;
    this.words = new Uint32Array(64);
  }
  update(bytes) {
    this.length += bytes.length;
    let offset = 0;
    if (this.used) {
      const count = Math.min(64 - this.used, bytes.length);
      this.pending.set(bytes.subarray(0, count), this.used);
      this.used += count; offset += count;
      if (this.used === 64) { this.block(this.pending, 0); this.used = 0; }
    }
    while (offset + 64 <= bytes.length) { this.block(bytes, offset); offset += 64; }
    if (offset < bytes.length) { this.pending.set(bytes.subarray(offset), 0); this.used = bytes.length - offset; }
    return this;
  }
  block(bytes, offset) {
    const w = this.words, k = IncrementalSha256.constants;
    const r = (x,n) => (x >>> n) | (x << (32-n));
    for (let i=0;i<16;i++) { const p=offset+i*4; w[i]=(bytes[p]<<24)|(bytes[p+1]<<16)|(bytes[p+2]<<8)|bytes[p+3]; }
    for (let i=16;i<64;i++) {
      const a=w[i-15], b=w[i-2];
      w[i]=(w[i-16]+(r(a,7)^r(a,18)^(a>>>3))+w[i-7]+(r(b,17)^r(b,19)^(b>>>10)))>>>0;
    }
    let [a,b,c,d,e,f,g,h]=this.state;
    for (let i=0;i<64;i++) {
      const t1=(h+(r(e,6)^r(e,11)^r(e,25))+((e&f)^(~e&g))+k[i]+w[i])>>>0;
      const t2=((r(a,2)^r(a,13)^r(a,22))+((a&b)^(a&c)^(b&c)))>>>0;
      h=g;g=f;f=e;e=(d+t1)>>>0;d=c;c=b;b=a;a=(t1+t2)>>>0;
    }
    [a,b,c,d,e,f,g,h].forEach((v,i)=>this.state[i]=(this.state[i]+v)>>>0);
  }
  digest() {
    const padding = new Uint8Array(this.used < 56 ? 64 : 128);
    padding.set(this.pending.subarray(0,this.used)); padding[this.used]=0x80;
    const view=new DataView(padding.buffer);
    view.setUint32(padding.length-8, Math.floor(this.length/0x20000000));
    view.setUint32(padding.length-4, (this.length*8)>>>0);
    for(let i=0;i<padding.length;i+=64) this.block(padding,i);
    return Array.from(this.state, n=>n.toString(16).padStart(8,"0")).join("");
  }
}
IncrementalSha256.constants = new Uint32Array([
  0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
  0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
  0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
  0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
  0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
  0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
  0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
  0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2
]);
globalThis.IncrementalSha256 = IncrementalSha256;
if (typeof self !== "undefined") self.onmessage = async ({data}) => {
  try {
    const hash = new IncrementalSha256();
    const size = data.chunkSize || 2*1024*1024;
    const chunks = [];
    for (let offset=0;offset<data.file.size;offset+=size) {
      const bytes=new Uint8Array(await data.file.slice(offset,offset+size).arrayBuffer());
      hash.update(bytes);
      chunks.push(Array.from(new Uint8Array(await crypto.subtle.digest("SHA-256",bytes)), n=>n.toString(16).padStart(2,"0")).join(""));
      self.postMessage({progress:Math.min(offset+size,data.file.size)});
    }
    self.postMessage({sha256:hash.digest(),chunks,chunkSize:size});
  } catch (error) { self.postMessage({error:String(error.message || error)}); }
};
