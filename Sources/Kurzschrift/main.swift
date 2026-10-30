// The same executable doubles as the speaker-diarization helper process (see Diarization.swift)
// and as a command-line tool for rebuilding transcripts from kept recordings (see Reprocess.swift)
// or comparing them with another recognizer (see ParakeetProbe.swift).
Diarization.runHelperIfRequested()
ParakeetProbe.runIfRequested()
MainActor.assumeIsolated { Reprocess.runIfRequested() }
KurzschriftApp.main()
