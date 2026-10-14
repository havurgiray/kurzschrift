import AVFoundation
import AudioTeeCore
import CoreAudio
import Foundation

enum AudioSourceKind: String, Hashable, CaseIterable, Codable {
  case system
  case microphone
}

/// Every track is stored and analyzed as 16 kHz mono Int16, which is what SpeechAnalyzer prefers.
let sessionAudioFormat = AVAudioFormat(
  commonFormat: .pcmFormatInt16, sampleRate: 16_000, channels: 1, interleaved: true)!

enum CaptureError: LocalizedError {
  case systemAudio(String)
  case noMicrophone

  var errorDescription: String? {
    switch self {
    case .systemAudio(let detail): "Couldn't capture system audio: \(detail)"
    case .noMicrophone: "No usable microphone was found."
    }
  }
}

// MARK: - Track

/// One captured stream. Converts to the session format, keeps the stream aligned with the session
/// clock (padding silence after gaps such as a microphone switch), records it to a WAV file, and
/// hands every buffer on to speech recognition. All sources share one timeline, which is what
/// lets words be matched to speakers and the two sources be merged in order afterwards.
final class Track: @unchecked Sendable {
  let kind: AudioSourceKind
  let fileURL: URL
  private let file: AVAudioFile
  private let queue: DispatchQueue
  private let sessionStart: Date
  private let onAudio: (AVAudioPCMBuffer) -> Void
  private var converter: AVAudioConverter?
  private var samplesWritten: Int64 = 0
  private var closed = false
  private var signalSeen = false

  init(kind: AudioSourceKind, fileURL: URL, sessionStart: Date, onAudio: @escaping (AVAudioPCMBuffer) -> Void) throws {
    self.kind = kind
    self.fileURL = fileURL
    self.sessionStart = sessionStart
    self.onAudio = onAudio
    queue = DispatchQueue(label: "Kurzschrift.track.\(kind.rawValue)", qos: .userInitiated)
    file = try AVAudioFile(
      forWriting: fileURL, settings: sessionAudioFormat.settings, commonFormat: .pcmFormatInt16, interleaved: true)
  }

  /// Accepts audio in any format. Must be called from one thread at a time (each source's own).
  func receive(_ buffer: AVAudioPCMBuffer) {
    guard let converted = convert(buffer) else { return }
    queue.async { [self] in
      guard !closed else { return }
      let arrivalSample = Int64(Date().timeIntervalSince(sessionStart) * sessionAudioFormat.sampleRate)
      let gap = arrivalSample - Int64(converted.frameLength) - samplesWritten
      if gap > Int64(sessionAudioFormat.sampleRate / 2) {
        appendSilence(frames: gap)
      }
      if !signalSeen, let data = converted.int16ChannelData?[0] {
        signalSeen = (0..<Int(converted.frameLength)).contains { data[$0] != 0 }
      }
      append(converted)
    }
  }

  /// Waits for queued audio, then stops accepting more and closes the file.
  func close() {
    queue.sync {
      guard !closed else { return }
      closed = true
      file.close()
    }
  }

  /// False while only digital silence has arrived, which for system audio usually means the
  /// recording permission is missing.
  var hasSignal: Bool {
    queue.sync { signalSeen }
  }

  var duration: Double {
    queue.sync { Double(samplesWritten) / sessionAudioFormat.sampleRate }
  }

  private func append(_ buffer: AVAudioPCMBuffer) {
    do {
      try file.write(from: buffer)
    } catch {
      Log.write("writing \(kind) audio failed: \(error)")
    }
    onAudio(buffer)
    samplesWritten += Int64(buffer.frameLength)
  }

  private func appendSilence(frames: Int64) {
    var remaining = frames
    while remaining > 0 {
      let count = AVAudioFrameCount(min(remaining, 16_000))
      guard let silence = AVAudioPCMBuffer(pcmFormat: sessionAudioFormat, frameCapacity: count) else { return }
      silence.frameLength = count
      memset(silence.int16ChannelData![0], 0, Int(count) * MemoryLayout<Int16>.size)
      append(silence)
      remaining -= Int64(count)
    }
  }

  private func convert(_ buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
    if buffer.format == sessionAudioFormat { return buffer }
    if converter == nil || converter?.inputFormat != buffer.format {
      converter = AVAudioConverter(from: buffer.format, to: sessionAudioFormat)
      converter?.downmix = true
    }
    guard let converter else { return nil }
    let ratio = sessionAudioFormat.sampleRate / buffer.format.sampleRate
    let capacity = AVAudioFrameCount((Double(buffer.frameLength) * ratio).rounded(.up)) + 64
    guard let out = AVAudioPCMBuffer(pcmFormat: sessionAudioFormat, frameCapacity: capacity) else { return nil }
    var consumed = false
    var error: NSError?
    converter.convert(to: out, error: &error) { _, status in
      if consumed {
        status.pointee = .noDataNow
        return nil
      }
      consumed = true
      status.pointee = .haveData
      return buffer
    }
    return error == nil && out.frameLength > 0 ? out : nil
  }
}

// MARK: - System audio (Core Audio process tap)

private struct QuietLogger: AudioTeeLogger {
  func debug(_ message: String, context: [String: String]?) {}
  func info(_ message: String, context: [String: String]?) {}
  func error(_ message: String, context: [String: String]?) {
    Log.write("audio tap: \(message) \(context ?? [:])")
  }
}

/// Captures everything the Mac plays (all apps), without muting it and without a virtual device.
final class SystemAudioSource: AudioOutputHandler {
  private let track: Track
  private var manager: AudioTapManager?
  private var recorder: AudioRecorder?
  private var format: AVAudioFormat?

  init(track: Track) {
    self.track = track
  }

  func start() throws {
    AudioTeeLogging.logger = QuietLogger()
    let manager = AudioTapManager()
    do {
      try manager.setupAudioTap(
        with: TapConfiguration(processes: [], muteBehavior: .unmuted, isExclusive: true, isMono: true))
    } catch {
      throw CaptureError.systemAudio(String(describing: error))
    }
    guard let deviceID = manager.getDeviceID() else { throw CaptureError.systemAudio("no capture device") }
    let recorder = try AudioRecorder(
      deviceID: deviceID, outputHandler: self, convertToSampleRate: sessionAudioFormat.sampleRate, chunkDuration: 0.1)
    var description = recorder.outputFormat
    format = AVAudioFormat(streamDescription: &description)
    try recorder.startRecording()
    self.manager = manager
    self.recorder = recorder
  }

  func stop() {
    recorder?.stopRecording()
    recorder = nil
    manager = nil
  }

  func handleMetadata(_ metadata: AudioStreamMetadata) {}
  func handleStreamStart() {}
  func handleStreamStop() {}

  func handleAudioData(_ pointer: UnsafeRawPointer, count: Int) {
    guard let format else { return }
    let bytesPerFrame = Int(format.streamDescription.pointee.mBytesPerFrame)
    let frames = AVAudioFrameCount(count / bytesPerFrame)
    guard frames > 0, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames) else { return }
    buffer.frameLength = frames
    memcpy(buffer.mutableAudioBufferList.pointee.mBuffers.mData, pointer, Int(frames) * bytesPerFrame)
    track.receive(buffer)
  }
}

// MARK: - Kept recording

/// Saves a finished track next to the transcript. Speech recognition and speaker separation
/// always work from the full-quality WAV; the copy you keep is compressed to AAC (.m4a, plays
/// anywhere), or stays the original WAV when the bit rate is 0.
enum KeptRecording {
  /// Returns the saved file, or nil if it couldn't be saved. Falls back to the WAV if compressing fails.
  static func save(_ wav: URL, as base: URL, kbps: Int) async -> URL? {
    if kbps > 0 {
      let target = base.appendingPathExtension("m4a")
      do {
        try await Task.detached(priority: .userInitiated) { try compress(wav, to: target, kbps: kbps) }.value
        return target
      } catch {
        Log.write("compressing \(wav.lastPathComponent) failed: \(error); keeping the WAV")
        try? FileManager.default.removeItem(at: target)
      }
    }
    let target = base.appendingPathExtension("wav")
    return (try? FileManager.default.moveItem(at: wav, to: target)) != nil ? target : nil
  }

  /// About 2 s per hour of audio on an M4.
  static func compress(_ wav: URL, to target: URL, kbps: Int) throws {
    let input = try AVAudioFile(forReading: wav, commonFormat: .pcmFormatInt16, interleaved: true)
    let settings: [String: Any] = [
      AVFormatIDKey: kAudioFormatMPEG4AAC,
      AVSampleRateKey: input.fileFormat.sampleRate,
      AVNumberOfChannelsKey: input.fileFormat.channelCount,
      AVEncoderBitRateKey: kbps * 1000,
    ]
    let output = try AVAudioFile(forWriting: target, settings: settings, commonFormat: .pcmFormatInt16, interleaved: true)
    defer { output.close() }
    guard let buffer = AVAudioPCMBuffer(pcmFormat: input.processingFormat, frameCapacity: 160_000) else { return }
    while input.framePosition < input.length {
      try input.read(into: buffer)
      try output.write(from: buffer)
    }
  }
}

// MARK: - Audio file (testing)

/// Plays an audio file into a track in real time instead of capturing system audio
/// (kurzschrift://start?mode=…&audio=/path/file.wav), so a whole meeting can be replayed without
/// any recording permission.
final class FileAudioSource: @unchecked Sendable {
  private let track: Track
  private let url: URL
  private var task: Task<Void, Never>?

  init(track: Track, url: URL) {
    self.track = track
    self.url = url
  }

  func start() throws {
    let file = try AVAudioFile(forReading: url)
    let track = track
    task = Task.detached(priority: .userInitiated) {
      let chunk = AVAudioFrameCount(file.processingFormat.sampleRate / 10)
      while !Task.isCancelled, file.framePosition < file.length {
        let frames = min(chunk, AVAudioFrameCount(file.length - file.framePosition))
        guard let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: frames),
          (try? file.read(into: buffer, frameCount: frames)) != nil
        else { break }
        track.receive(buffer)
        try? await Task.sleep(for: .milliseconds(100))
      }
    }
  }

  func stop() {
    task?.cancel()
  }
}

// MARK: - Microphone

final class MicrophoneSource {
  private let track: Track
  private let deviceUID: String
  private let engine = AVAudioEngine()
  private var configurationObserver: NSObjectProtocol?

  init(track: Track, deviceUID: String) {
    self.track = track
    self.deviceUID = deviceUID
  }

  func start() throws {
    try startEngine()
    // Fires when the input device changes (headset plugged in, default mic switched...).
    configurationObserver = NotificationCenter.default.addObserver(
      forName: .AVAudioEngineConfigurationChange, object: engine, queue: .main
    ) { [weak self] _ in
      self?.restart()
    }
  }

  func stop() {
    if let configurationObserver { NotificationCenter.default.removeObserver(configurationObserver) }
    configurationObserver = nil
    engine.stop()
    engine.inputNode.removeTap(onBus: 0)
  }

  private func startEngine() throws {
    let input = engine.inputNode
    if !deviceUID.isEmpty, var deviceID = AudioDevices.deviceID(forUID: deviceUID), let unit = input.audioUnit {
      // A chosen microphone that isn't connected right now falls back to the system default.
      AudioUnitSetProperty(
        unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &deviceID,
        UInt32(MemoryLayout<AudioDeviceID>.size))
    }
    let format = input.outputFormat(forBus: 0)
    guard format.sampleRate > 0, format.channelCount > 0 else { throw CaptureError.noMicrophone }
    let track = track
    input.installTap(onBus: 0, bufferSize: 4096, format: format) { buffer, _ in
      track.receive(buffer)
    }
    engine.prepare()
    try engine.start()
  }

  private func restart() {
    engine.stop()
    engine.inputNode.removeTap(onBus: 0)
    do {
      try startEngine()
    } catch {
      Log.write("restarting microphone failed: \(error)")
    }
  }
}

// MARK: - Device list

enum AudioDevices {
  struct Device: Identifiable, Hashable {
    let id: AudioDeviceID
    let uid: String
    let name: String
  }

  static func inputDevices() -> [Device] {
    allDeviceIDs().compactMap { id in
      guard inputChannelCount(id) > 0, let uid = stringProperty(id, kAudioDevicePropertyDeviceUID),
        let name = stringProperty(id, kAudioObjectPropertyName), !uid.hasPrefix("audiotee")
      else { return nil }
      return Device(id: id, uid: uid, name: name)
    }
  }

  static func deviceID(forUID uid: String) -> AudioDeviceID? {
    inputDevices().first { $0.uid == uid }?.id
  }

  static func defaultInputName() -> String? {
    var address = AudioObjectPropertyAddress(
      mSelector: kAudioHardwarePropertyDefaultInputDevice, mScope: kAudioObjectPropertyScopeGlobal,
      mElement: kAudioObjectPropertyElementMain)
    var id = AudioDeviceID(0)
    var size = UInt32(MemoryLayout<AudioDeviceID>.size)
    guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &id) == noErr
    else { return nil }
    return stringProperty(id, kAudioObjectPropertyName)
  }

  private static func allDeviceIDs() -> [AudioDeviceID] {
    var address = AudioObjectPropertyAddress(
      mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal,
      mElement: kAudioObjectPropertyElementMain)
    var size: UInt32 = 0
    let system = AudioObjectID(kAudioObjectSystemObject)
    guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr else { return [] }
    var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
    guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &ids) == noErr else { return [] }
    return ids
  }

  private static func inputChannelCount(_ id: AudioDeviceID) -> Int {
    var address = AudioObjectPropertyAddress(
      mSelector: kAudioDevicePropertyStreamConfiguration, mScope: kAudioObjectPropertyScopeInput,
      mElement: kAudioObjectPropertyElementMain)
    var size: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(id, &address, 0, nil, &size) == noErr, size > 0 else { return 0 }
    let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<AudioBufferList>.alignment)
    defer { raw.deallocate() }
    guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, raw) == noErr else { return 0 }
    let list = UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self))
    return list.reduce(0) { $0 + Int($1.mNumberChannels) }
  }

  private static func stringProperty(_ id: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> String? {
    var address = AudioObjectPropertyAddress(
      mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    var value: Unmanaged<CFString>?
    var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
    guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr, let value else { return nil }
    return value.takeRetainedValue() as String
  }
}
