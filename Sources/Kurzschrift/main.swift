// The same executable doubles as the speaker-diarization helper process (see Diarization.swift)
// and as a command-line tool for rebuilding transcripts from kept recordings (see Reprocess.swift).
Diarization.runHelperIfRequested()
MainActor.assumeIsolated { Reprocess.runIfRequested() }
KurzschriftApp.main()
