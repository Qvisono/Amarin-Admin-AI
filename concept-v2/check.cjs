const {chromium}=require('C:/Users/RRR/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const sharp=require('C:/Users/RRR/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/sharp');
const path=require('path');
(async()=>{
 const browser=await chromium.launch({headless:true,executablePath:'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'});
 const page=await browser.newPage({viewport:{width:1000,height:1000},deviceScaleFactor:2});const errors=[];page.on('pageerror',e=>errors.push(e.message));
 const url='file:///'+path.resolve(__dirname,'ready.html').replace(/\\/g,'/');
 const checks=[];
 for(const w of [320,360,390,430]){
  await page.goto(url+'?shot=1&w='+w);await page.locator('.app').screenshot({path:path.join(__dirname,'chat-'+w+'.png')});
  const result=await page.evaluate(()=>{const a=document.querySelector('.app'),c=document.querySelector('.composer'),t=document.querySelector('.toolbar'),els=[...t.children].map(e=>{let r=e.getBoundingClientRect();return {x:r.x,right:r.right,w:r.width,h:r.height}});return {width:a.clientWidth,overflow:a.scrollWidth>a.clientWidth,composerHeight:c.getBoundingClientRect().height,targets:els,noOverlap:els.every((e,i)=>!i||e.x>=els[i-1].right)}});
  if(result.overflow||!result.noOverlap)throw Error('Layout failed '+w);checks.push(result);
  await page.locator('#openSidebar').click();await page.waitForTimeout(240);await page.locator('.app').screenshot({path:path.join(__dirname,'sidebar-'+w+'.png')});
  const size=await page.locator('#newChat').boundingBox();if(size.width!==size.height)throw Error('New chat not square');
  await page.locator('#search').fill('море');if(await page.locator('.chat-row').count()!==0)throw Error('Unexpected filter result');
  await page.locator('#search').fill('моря');if(await page.locator('.chat-row').count()!==1)throw Error('Search failed');
  await page.locator('#newChat').click();if(await page.locator('.hero').count()!==1)throw Error('New chat failed');
  await page.locator('#message').fill('Тестовое сообщение');await page.locator('#send').click();if(await page.locator('.user-msg').last().innerText()!=='Тестовое сообщение')throw Error('Send failed');
 }
 await page.goto(url+'?shot=1&w=390&state=empty');await page.locator('.app').screenshot({path:path.join(__dirname,'empty-390.png')});
 await page.locator('#model').click();await page.locator('[data-effort="Low"]').click();await page.locator('#apply').click();if(await page.locator('.effort').innerText()!=='· Low')throw Error('Effort failed');
 await page.locator('#metrics').click();if(!await page.locator('.sheet h2').isVisible())throw Error('Metrics failed');await page.keyboard.press('Escape');
 await page.goto(url+'?shot=1&w=390&state=typed');await page.locator('.app').screenshot({path:path.join(__dirname,'typed-390.png')});await page.locator('.composer').screenshot({path:path.join(__dirname,'composer-detail.png')});
 await page.goto(url+'?shot=1&w=390&state=sidebar');await page.waitForTimeout(240);await page.locator('#settings').click();await page.locator('.app').screenshot({path:path.join(__dirname,'settings-390.png')});
 if(errors.length)throw Error(errors.join('\n'));
 const frames=['empty-390.png','chat-390.png','sidebar-390.png'];
 await sharp({create:{width:2508,height:1816,channels:4,background:'#08090a'}}).composite(await Promise.all(frames.map(async(f,i)=>({input:await sharp(path.join(__dirname,f)).png().toBuffer(),left:36+i*828,top:146})))).png().toFile(path.join(__dirname,'mobile-screens-v2.png'));
 require('fs').writeFileSync(path.join(__dirname,'layout-checks.json'),JSON.stringify({errors,checks,tested:['320/360/390/430 widths','sidebar search','square new chat','new chat','send local message','reasoning selection','context/balance sheet','settings navigation'],scope:'HTML concept only; not Android/iOS verification'},null,2));
 console.log(JSON.stringify({errors,checks:checks.map(x=>({width:x.width,composerHeight:x.composerHeight,overflow:x.overflow,noOverlap:x.noOverlap}))}));await browser.close();
})().catch(e=>{console.error(e);process.exit(1)});
