import subprocess, sys
from email.message import EmailMessage
from email.utils import formatdate, make_msgid

PATH='M15.995 9.772L7.46 5.413C4.96 4.143 2 5.953 2 8.753V16.733C2 19.533 4.96 21.353 7.46 20.073L8.149 19.721L5.31 27.084C5.08 27.671 5.69 28.237 6.26 27.979L8.52 26.965C8.88 26.806 9.3 26.965 9.46 27.323L10.45 29.579C10.7 30.155 11.53 30.135 11.76 29.549L15.99 18.571L20.22 29.549C20.45 30.135 21.28 30.155 21.53 29.579L22.52 27.323C22.68 26.965 23.1 26.806 23.46 26.965L25.72 27.979C26.29 28.237 26.9 27.671 26.67 27.084L23.828 19.715L24.53 20.073C27.03 21.353 29.99 19.533 29.99 16.733V8.753C29.99 5.953 27.03 4.143 24.53 5.413Z'
FOLD='M12.18 17.667L7.45 20.083C5.09 21.293 2.3 19.723 2.02 17.173C2.3 14.623 5.08 13.053 7.45 14.263L12 16.586Z'
KNOT='<rect x="12" y="6" width="8" height="13" rx="2.2"'
# As the email-bow prototype draws it: 26px, 1.5px white edge, folds kept.
u=32/26; e=1.5*u; pad=max(0,e-1.6); box=32+2*pad
st=f' fill="#FFFFFF" stroke="#FFFFFF" stroke-width="{2*e}" stroke-linejoin="round"'
svg=(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{-pad} {-pad} {box} {box}" width="52" height="52">'
     f'<path d="{PATH}"{st}/>{KNOT}{st}/>'
     f'<path d="{PATH}" fill="#3F6E8C"/>'
     f'<path d="{FOLD}" fill="#1E1A16" fill-opacity=".3"/><path d="{FOLD}" transform="translate(31.99 0) scale(-1 1)" fill="#1E1A16" fill-opacity=".3"/>'
     f'{KNOT} fill="#1E1A16"/></svg>')
open('bow.svg','w').write(svg)
subprocess.run(['rsvg-convert','-w','52','-h','52','bow.svg','-o','bow.png'],check=True)
png=open('bow.png','rb').read()

EV='Cousins’ Swap'
SUBJECT=f'{EV}: names are drawn'
LINK='https://pollyanna.example/events/7'
ACCT='https://pollyanna.example/account'
TEXT=f'Names are drawn in “{EV}” on Pollyanna, and you can see who you’re giving to.'
F1=f'You’re getting this because you take part in “{EV}” on Pollyanna.'
F2='To stop Pollyanna emails, switch them off on your account page:'
plain=f'{TEXT}\n\n{LINK}\n\n-- \n{F1}\n{F2} {ACCT}\n'

W,INK,SOFT,PALE='#FFFFFF','#1E1A16','#5B5650','#DCE7EE'
FONT="-apple-system, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif"
HID='display:none; font-size:1px; line-height:1px; max-height:0; max-width:0; opacity:0; overflow:hidden; mso-hide:all;'
T='role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0"'
html=f'''<!DOCTYPE html>
<html lang="en" dir="ltr">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="light only">
<meta name="supported-color-schemes" content="light only">
<title>{SUBJECT}</title>
</head>
<body style="margin:0; padding:0; background-color:{W};">
<div lang="en" dir="ltr" style="margin:0; padding:0; background-color:{W}; color:{INK}; color-scheme:light only; font-family:{FONT}; font-size:16px; line-height:1.5;">
<div aria-hidden="true" style="{HID}">{TEXT}</div>
<div aria-hidden="true" style="{HID}">{"&#847;&zwnj;&nbsp;"*100}</div>
<table {T} style="border-collapse:collapse; background-color:{W};">
<tr>
<td align="center" style="background-color:{W}; padding:16px 24px;">
<table {T} style="border-collapse:collapse; max-width:520px;">
<tr>
<td style="background-color:{W};">
<table role="presentation" cellpadding="0" cellspacing="0" border="0" style="border-collapse:collapse;">
<tr>
<td width="26" valign="middle" style="padding:0 8px 0 0; line-height:0; font-size:0;"><img src="cid:__CID__" width="26" height="26" alt="" style="display:block; border:0; width:26px; height:26px;"></td>
<td valign="middle" style="font-family:{FONT}; color:{INK}; font-size:22px; line-height:26px; font-weight:800; letter-spacing:-0.4px;">pollyanna</td>
</tr>
</table>
</td>
</tr>
</table>
</td>
</tr>
<tr>
<td align="center" style="background-color:{PALE}; padding:24px 16px 40px 16px;">
<table {T} style="border-collapse:separate; max-width:520px;">
<tr>
<td style="background-color:{W}; border-radius:10px; padding:24px;">
<h1 style="margin:0 0 12px 0; font-family:{FONT}; color:{INK}; font-size:26px; line-height:1.15; font-weight:800; letter-spacing:-0.5px;">{EV}</h1>
<p style="margin:0 0 20px 0; font-family:{FONT}; color:{INK}; font-size:16px; line-height:1.5;">{TEXT}</p>
<table {T} style="border-collapse:separate;">
<tr>
<td align="center" style="background-color:{INK}; border-radius:10px;"><a href="{LINK}" style="display:block; padding:13px 18px; font-family:{FONT}; font-size:18px; line-height:1.3; font-weight:800; color:{W}; text-decoration:none; border-radius:10px;">Open the event</a></td>
</tr>
</table>
<p style="margin:12px 0 0 0; font-family:{FONT}; color:{SOFT}; font-size:14px; line-height:1.5; word-break:break-all;">{LINK}</p>
</td>
</tr>
</table>
</td>
</tr>
<tr>
<td align="center" style="background-color:{W}; padding:20px 24px 28px 24px;">
<table {T} style="border-collapse:collapse; max-width:520px;">
<tr>
<td style="background-color:{W};">
<p style="margin:0 0 4px 0; font-family:{FONT}; color:{SOFT}; font-size:14px; line-height:1.5;">{F1}</p>
<p style="margin:0; font-family:{FONT}; color:{SOFT}; font-size:14px; line-height:1.5; word-break:break-word;">{F2} <a href="{ACCT}" style="color:{INK}; font-weight:600; text-decoration:underline;">{ACCT}</a></p>
</td>
</tr>
</table>
</td>
</tr>
</table>
</div>
</body>
</html>
'''
def build(sender, to):
    m=EmailMessage()
    m['From']=sender; m['To']=to; m['Subject']=SUBJECT
    m['Date']=formatdate(localtime=True)
    dom=sender.split('@')[-1].strip('>')
    m['Message-ID']=make_msgid(domain=dom)
    cid=make_msgid(domain=dom)
    m.set_content(plain, cte='quoted-printable')
    m.add_alternative(html.replace('__CID__',cid[1:-1]), subtype='html', cte='quoted-printable')
    m.get_payload()[1].add_related(png,'image','png',cid=cid,disposition='inline',filename='bow.png')
    return m
if __name__=='__main__':
    m=build(sys.argv[1],sys.argv[2])
    open('sample.eml','wb').write(m.as_bytes())
    def walk(p,d=0):
        print('  '*d+p.get_content_type(), p.get('Content-ID',''), p.get('Content-Disposition',''))
        if p.is_multipart():
            for c in p.get_payload(): walk(c,d+1)
    walk(m); print(len(png),'png bytes;',len(m.as_bytes()),'message bytes')
