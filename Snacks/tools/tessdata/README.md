# Bundled Tesseract language data

Drop `*.traineddata` files here to have them ship with the Snacks build and be
available for OCR without a first-run download.

## Source

Files in this directory are expected to come from
[tessdata_best](https://github.com/tesseract-ocr/tessdata_best) (Apache 2.0
licensed). `tessdata_best` is what pgsrip and the broader subtitle-rip
ecosystem default to — full-movie OCR still finishes in under two minutes and
the accuracy win on stylised / low-contrast subtitles is worth it.
`tessdata_fast` is an option if a deployment is storage-constrained, but it's
not the default.

## Recommended bundled set (~106 MB)

```
eng.traineddata      # English
spa.traineddata      # Spanish
fra.traineddata      # French
deu.traineddata      # German
ita.traineddata      # Italian
por.traineddata      # Portuguese
rus.traineddata      # Russian
jpn.traineddata      # Japanese
chi_sim.traineddata  # Chinese (Simplified)
osd.traineddata      # Orientation/script detection — small, always useful
```

Any language not bundled is downloaded on-demand into
`{SNACKS_WORK_DIR}/tools/tessdata/` on first use (see
[TessdataResolver.cs](../../Services/Ocr/TessdataResolver.cs)).
