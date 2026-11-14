"""Builds the third-party notices that ship with Kurzschrift: notices/mac.txt (copied into the Mac app)
and notices/windows.txt (installed next to Kurzschrift.exe).

usage: python3 notices/make.py          (needs network; run again when a dependency changes)

Every license text comes from the exact upstream version Kurzschrift uses (or from the vendored copy
in Vendor/), so the notices match what is compiled in or shipped.
"""
import io
import os
import sys
import urllib.request
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GH = "https://raw.githubusercontent.com"


def fetch(url):
    req = urllib.request.Request(url, headers={"User-Agent": "Kurzschrift notices"})
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.read()


def text(url):
    return fetch(url).decode("utf-8-sig").replace("\r\n", "\n").strip()


def local(path):
    with open(os.path.join(ROOT, path), encoding="utf-8-sig") as f:
        return f.read().replace("\r\n", "\n").strip()


def nupkg_files(package, version, names):
    data = fetch(f"https://api.nuget.org/v3-flatcontainer/{package}/{version}/{package}.{version}.nupkg")
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        found = {n.lower(): n for n in z.namelist()}
        return [z.read(found[n.lower()]).decode("utf-8-sig").replace("\r\n", "\n").strip() for n in names]


def section(title, body):
    bar = "=" * 78
    return f"{bar}\n{title}\n{bar}\n\n{body}\n\n"


# Models are downloaded by the app at first use; they're listed for attribution, not shipped.
MODELS_WINDOWS = """\
- NVIDIA Parakeet TDT 0.6B v3 (speech recognition), CC BY 4.0, by NVIDIA:
  https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3
  int8 ONNX conversion by the k2-fsa project:
  https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8
- Silero VAD (speech detection), MIT, by the Silero Team: https://github.com/snakers4/silero-vad
- OPUS-MT (translation), by the Language Technology Research Group at the University of Helsinki:
  https://github.com/Helsinki-NLP/Opus-MT. One model per language pair, under the Apache License 2.0
  or CC BY 4.0 depending on the pair (each pair's model card at https://huggingface.co/Helsinki-NLP
  says which; for example de-en is Apache 2.0 and en-de is CC BY 4.0). Quantized ONNX exports by
  Xenova (https://huggingface.co/Xenova), under the same license as the original pair.
- pyannote segmentation 3.0 (speaker separation), MIT, by Hervé Bredin / pyannote:
  https://huggingface.co/pyannote/segmentation-3.0; ONNX conversion by the k2-fsa project:
  https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0
- WeSpeaker ResNet34-LM trained on VoxCeleb (speaker embeddings), CC BY 4.0 like the VoxCeleb data
  it was trained on, by the WeNet community: https://github.com/wenet-e2e/wespeaker
"""

MODELS_MAC = """\
- NVIDIA Parakeet TDT 0.6B v3 (speech recognition), CC BY 4.0, by NVIDIA:
  https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3; modified Core ML conversion by Fluid Inference:
  https://huggingface.co/FluidInference/parakeet-tdt-0.6b-v3-coreml
- pyannote speaker-diarization-community-1 (speaker separation), CC BY 4.0, with WeSpeaker speaker
  embeddings and PLDA parameters from BUT Speech@FIT; modified Core ML conversion by Fluid Inference:
  https://huggingface.co/FluidInference/speaker-diarization-coreml
"""

HEADER = """\
Kurzschrift for {platform}: third-party notices

Kurzschrift is free software under the GNU Affero General Public License v3.0 (see LICENSE).
Its source code: https://github.com/havurgiray/kurzschrift

This program includes the third-party software listed below, each under the license shown. The
full license texts follow the list.

{components}
Models downloaded by the app on first use (not part of this download):

{models}
"""


def windows():
    apache = text(f"{GH}/k2-fsa/sherpa-onnx/v2.0.1/LICENSE")
    ort_license, ort_notices = nupkg_files("microsoft.ml.onnxruntime.managed", "1.28.0",
                                           ["LICENSE.txt", "ThirdPartyNotices.txt"])
    components = """\
- .NET 10 runtime, Windows Presentation Foundation and Windows Forms, MIT, by the .NET Foundation and
  Contributors: https://github.com/dotnet (with their third-party notices below)
- ONNX Runtime 1.28 (onnxruntime.dll and its .NET API), MIT, by Microsoft Corporation:
  https://github.com/microsoft/onnxruntime (with its third-party notices below)
- sherpa-onnx 2.0.1 (sherpa-onnx-c-api.dll and its .NET API), Apache License 2.0, by the k2-fsa
  project (Xiaomi Corporation and contributors): https://github.com/k2-fsa/sherpa-onnx
  Compiled into sherpa-onnx-c-api.dll:
  - kaldi-native-fbank 1.22.4, Apache License 2.0: https://github.com/csukuangfj/kaldi-native-fbank
  - kaldi-decoder 0.3.0, Apache License 2.0: https://github.com/k2-fsa/kaldi-decoder
  - kaldifst 1.8.0, Apache License 2.0: https://github.com/k2-fsa/kaldifst
  - OpenFst 1.8.5, Apache License 2.0: https://github.com/csukuangfj/openfst
  - simple-sentencepiece 0.7, Apache License 2.0: https://github.com/pkufool/simple-sentencepiece
  - nlohmann/json 3.12.0, MIT: https://github.com/nlohmann/json
  - utf8proc 2.12.0, MIT and the Unicode data license: https://github.com/JuliaStrings/utf8proc
  - hclust-cpp (fastcluster), BSD-style: https://github.com/csukuangfj/hclust-cpp
  - Eigen 5.0.1, Mozilla Public License 2.0. Its source code is available at
    https://gitlab.com/libeigen/eigen/-/tree/5.0.1
- NAudio 2.2.1 (NAudio.Core, NAudio.Wasapi), MIT, by Mark Heath and contributors:
  https://github.com/naudio/NAudio
- The installer is made with Inno Setup, by Jordan Russell and Martijn Laan: https://jrsoftware.org
"""
    out = HEADER.format(platform="Windows", components=components, models=MODELS_WINDOWS) + "\n"
    out += section("Apache License 2.0 (sherpa-onnx, kaldi-native-fbank, kaldi-decoder, kaldifst, OpenFst, "
                   "simple-sentencepiece)", apache)
    out += section("OpenFst notice", text(f"{GH}/csukuangfj/openfst/v1.8.5-2026-07-09/COPYING"))
    for name, url in [
        (".NET runtime: license", f"{GH}/dotnet/runtime/release/10.0/LICENSE.TXT"),
        (".NET runtime: third-party notices", f"{GH}/dotnet/runtime/release/10.0/THIRD-PARTY-NOTICES.TXT"),
        ("WPF: license", f"{GH}/dotnet/wpf/release/10.0/LICENSE.TXT"),
        ("WPF: third-party notices", f"{GH}/dotnet/wpf/release/10.0/THIRD-PARTY-NOTICES.TXT"),
        ("Windows Forms: license", f"{GH}/dotnet/winforms/release/10.0/LICENSE.TXT"),
        ("Windows Forms: third-party notices", f"{GH}/dotnet/winforms/release/10.0/THIRD-PARTY-NOTICES.TXT"),
    ]:
        out += section(name, text(url))
    out += section("ONNX Runtime: license", ort_license)
    out += section("ONNX Runtime: third-party notices", ort_notices)
    for name, url in [
        ("nlohmann/json (MIT)", f"{GH}/nlohmann/json/v3.12.0/LICENSE.MIT"),
        ("utf8proc (MIT and Unicode data license)", f"{GH}/JuliaStrings/utf8proc/v2.12.0/LICENSE.md"),
        ("hclust-cpp / fastcluster", f"{GH}/csukuangfj/hclust-cpp/2026-02-25/LICENSE"),
        ("Eigen (Mozilla Public License 2.0)", "https://gitlab.com/libeigen/eigen/-/raw/5.0.1/LICENSE"),
        ("NAudio (MIT)", f"{GH}/naudio/NAudio/v2.2.1/license.txt"),
        ("Inno Setup license", f"{GH}/jrsoftware/issrc/main/license.txt"),
        ("Silero VAD model (MIT)", f"{GH}/snakers4/silero-vad/master/LICENSE"),
    ]:
        out += section(name, text(url))
    return out


def mac():
    components = """\
- FluidAudio 0.15.7, Apache License 2.0, by Fluid Inference: https://github.com/FluidInference/FluidAudio
  (vendored in Vendor/FluidAudio), including fastcluster (BSD-style, Daniel Müllner and Google) and
  VBx (Apache License 2.0, BUT Speech@FIT)
- audiotee, MIT, by Nick Payne: https://github.com/makeusabrew/audiotee (vendored in Vendor/audiotee)
"""
    readme = local("Vendor/audiotee/README.md")
    mit = readme[readme.index("### The MIT License"):].strip()
    out = HEADER.format(platform="Mac", components=components, models=MODELS_MAC) + "\n"
    out += section("FluidAudio: Apache License 2.0", local("Vendor/FluidAudio/LICENSE"))
    out += section("fastcluster (in FluidAudio)", local("Vendor/FluidAudio/ThirdPartyLicenses/fastcluster-LICENSE.md"))
    out += section("VBx (in FluidAudio): Apache License 2.0", local("Vendor/FluidAudio/ThirdPartyLicenses/vbx-LICENSE.md"))
    out += section("audiotee (MIT)", mit)
    return out


for name, build in (("windows.txt", windows), ("mac.txt", mac)):
    path = os.path.join(ROOT, "notices", name)
    content = build()
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(content)
    print(f"{path}: {len(content) // 1024} KB")
