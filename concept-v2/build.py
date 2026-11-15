from pathlib import Path
import xml.etree.ElementTree as ET
import base64,json,re

work=Path(__file__).resolve().parent
project=Path(r'D:\Amarin Microsoft\Amarin Chat S')
xns='{http://schemas.microsoft.com/winfx/2006/xaml}'
tree=ET.parse(project/'assets/original/Theme/AiLogos.Dark.xaml').getroot()
kimi=next(n for n in tree if n.attrib.get(xns+'Key')=='Kimi')
path=next(n.attrib['Geometry'] for n in kimi.iter() if 'Geometry' in n.attrib)
path=re.sub(r'^F[01]\s*','',path)
main=ET.parse(project/'references/source/Amarin Admin AI/UI/MainWindow.xaml').getroot()
button=next(n for n in main.iter() if n.attrib.get(xns+'Name')=='SettingsButton')
gear=next(n.attrib['Data'] for n in button.iter() if 'Data' in n.attrib)
icon=base64.b64encode((project/'assets/original/jeklE-removebg-preview_1.ico').read_bytes()).decode()
groups=[]
for name,entries in [('ОСНОВНОЕ',[('Общие','General','#23874F'),('Оформление','Appearance','#C9467A'),('Профиль','Profile','#2F72E0')]),('МОДЕЛЬ',[('Модели','Models','#6A5CDB'),('Промпты','Prompts','#568F1A'),('Инструкции','Instructions','#1A8D9A')]),('ЗАЩИТА И ДАННЫЕ',[('Безопасность','Security','#9A45B5'),('Ключи и траты','Keys','#A88512'),('Данные','Data','#56738F'),('О программе','About','#7A7A82')])]:
    items=[]
    for title,key,color in entries:
        data=ET.parse(project/f'assets/navigation-svg/NavIcon.{key}.svg').getroot()[0].attrib['d']
        items.append(dict(title=title,color=color,path=data))
    groups.append(dict(name=name,items=items))
html=(work/'mobile-concept.html').read_text(encoding='utf-8').replace('__KIMI__',path).replace('__GEAR__',gear).replace('__SETTINGS__',json.dumps(groups,ensure_ascii=False))
html=re.sub(r'<svg class="(brand|hero-logo)" viewBox="0 0 24 24"><path d="__BRAND__" fill="currentColor"/></svg>',lambda m:f'<img class="{m[1]}" src="data:image/x-icon;base64,{icon}" alt=""/>',html)
assert '__BRAND__' not in html
(work/'ready.html').write_text(html,encoding='utf-8')
print('Concept ready')
