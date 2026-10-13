#!/usr/bin/env python3
"""Correct only codec VmHWM observations; primary timing/gate samples stay unchanged."""
import json
from pathlib import Path
import codec_measure

ROOT = Path(__file__).resolve().parent
OUT = ROOT / "outputs/micro"


def main():
    """Three warmups/three observations establish executable high water on the fixed corpus."""
    codec_measure.LIMITS[-1] = str(ROOT / "bin/codec-bench-rss")
    records = {}
    for fixture, codecs in [("photo.jpg",["jpeg-decoder","zune-jpeg","libjpeg-turbo-rs"]),
                            ("photo.webp",["image-webp","libwebp"])]:
        for codec in codecs:
            key=f"decode-{fixture}-{codec}"
            records[key]=codec_measure.invoke('rss-'+key,
                ["decode",codec,ROOT/'evidence/fixtures'/fixture,OUT/(key+'-rss.raw'),3])
    summary=json.loads((ROOT/'evidence/codec-summary.json').read_text())
    for profile, quality in [("web",90),("pdf",85)]:
        for fixture in ["photo.jpg","photo.webp"]:
            geometry=json.loads((OUT/f'{profile}-{fixture}.json').read_text())
            choices=[("jpeg-encoder",quality),("libjpeg-turbo-rs",quality)]
            if fixture.endswith('webp'):
                choices.extend([("libwebp-lossy",summary['matched_webp_quality'][profile]['webp_quality']),
                                ("image-webp-lossless",quality),("libwebp-lossless",quality)])
            for codec,q in choices:
                key=f"encode-{profile}-{fixture}-{codec}-q{q}"
                records[key]=codec_measure.invoke('rss-'+key,
                    ["encode",codec,OUT/f'{profile}-{fixture}.rgb',OUT/(key+'-rss.bin'),
                     geometry['width'],geometry['height'],q,3,3])
        print(f"Current-executable RSS complete: {profile}",flush=True)
    result={'method':'/proc/self/status VmHWM, after exec; three warmups/three observations; timing fields are supplemental only',
            'scope':'Only RSS correction; no performance-gate rerun',
            'rss_mib':{key:max(r['process_peak_rss_kib'] for r in rows if r['iteration']>=0)/1024
                       for key,rows in records.items()}}
    (ROOT/'evidence/codec-rss-corrected.json').write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result,indent=2))


if __name__ == '__main__':
    main()
