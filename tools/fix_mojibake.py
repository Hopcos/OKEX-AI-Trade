import sys
for p in sys.argv[1:]:
    b = open(p, 'rb').read()
    if b.startswith(b'\xef\xbb\xbf'): b = b[3:]
    s = b.decode('utf-8')
    try:
        fixed = s.encode('cp1252', errors='strict').decode('utf-8')
    except Exception as e:
        # undefined cp1252 bytes (0x81,0x8d,0x8f,0x90,0x9d) were mapped to same control chars
        raw = bytearray()
        for ch in s:
            o = ord(ch)
            try: raw += ch.encode('cp1252')
            except: raw.append(o) if o < 256 else (_ for _ in ()).throw(e)
        fixed = bytes(raw).decode('utf-8')
    open(p, 'wb').write(fixed.encode('utf-8'))
    print('fixed', p)
