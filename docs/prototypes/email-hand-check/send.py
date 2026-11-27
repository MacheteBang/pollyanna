"""Send the sample once. Settings come from the environment; the password from a file.
SMTP_HOST SMTP_PORT SMTP_TLS(starttls|tls|none) SMTP_USER SMTP_PASSWORD_FILE MAIL_FROM MAIL_TO"""
import os, smtplib, ssl
from build import build
e=os.environ
m=build(e['MAIL_FROM'], e['MAIL_TO'])
open('sent.eml','wb').write(m.as_bytes())
host,port,tls=e['SMTP_HOST'],int(e['SMTP_PORT']),e.get('SMTP_TLS','starttls')
ctx=ssl.create_default_context()
s=smtplib.SMTP_SSL(host,port,context=ctx,timeout=30) if tls=='tls' else smtplib.SMTP(host,port,timeout=30)
with s:
    if tls=='starttls': s.starttls(context=ctx)
    if e.get('SMTP_USER'):
        s.login(e['SMTP_USER'], open(os.path.expanduser(e['SMTP_PASSWORD_FILE'])).read().strip())
    refused=s.send_message(m)
print('sent', m['Message-ID'], 'refused:', refused)
