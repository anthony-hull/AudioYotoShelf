// --- Connection ---

export interface ConnectionStatus {
  id: string
  username: string
  absConnected: boolean
  audiobookshelfUrl: string
  yotoConnected: boolean
  yotoTokenExpiresAt: string | null
  defaultLibraryId: string | null
  defaultMinAge: number
  defaultMaxAge: number
  isAdmin: boolean
}

// --- Admin analytics ---

export interface AdminOverview {
  totalUsers: number
  absConnectedUsers: number
  yotoConnectedUsers: number
  adminUsers: number
  activeUsers7d: number
  activeUsers30d: number
  totalLogins: number
  logins7d: number
  logins30d: number
  totalTransfers: number
  completedTransfers: number
  failedTransfers: number
  transferSuccessRate: number
  transfers7d: number
  totalPlaylists: number
}

export interface AdminUser {
  id: string
  username: string
  isAdmin: boolean
  absConnected: boolean
  yotoConnected: boolean
  createdAt: string
  lastLoginAt: string | null
  loginCount: number
  transferCount: number
}

export interface UsagePoint {
  date: string
  logins: number
  transfers: number
}

/** Either a username + password or an Audiobookshelf API key. */
export interface AbsConnectRequest {
  /** Omitted when the server has its Audiobookshelf URL configured. */
  baseUrl?: string
  username?: string
  password?: string
  apiKey?: string
}

export interface AbsConnectOptions {
  isServerUrlLocked: boolean
}

export interface AbsConnectResponse {
  userConnectionId: string
  username: string
  absConnected: boolean
  yotoConnected: boolean
  defaultLibraryId: string | null
  libraries: string[] | null
}

// --- Libraries ---

export interface AbsLibrary {
  id: string
  name: string
  mediaType: string
}

export interface AbsLibraryItemsResponse {
  results: AbsLibraryItem[]
  total: number
  limit: number
  page: number
}

export interface AbsLibraryItem {
  id: string
  ino: string
  libraryId: string
  mediaType: string
  media: AbsBookMedia | null
  numFiles: number
  size: number
}

export interface AbsBookMedia {
  metadata: AbsBookMetadata
  coverPath: string | null
  audioFiles: AbsAudioFile[]
  chapters: AbsChapter[]
  duration: number
  numTracks: number
  numAudioFiles: number
  numChapters: number
}

export interface AbsBookMetadata {
  title: string | null
  subtitle: string | null
  authors: { id: string; name: string }[]
  narrators: string[]
  series: { id: string; name: string; sequence: string | null }[]
  genres: string[]
  publishedYear: string | null
  publisher: string | null
  description: string | null
  language: string | null
  explicit: boolean
}

export interface AbsAudioFile {
  index: number
  ino: string
  metadata: { filename: string; ext: string; path: string; size: number }
  duration: number
  codec: string
  bitRate: number
  format: string
  size: number
}

export interface AbsChapter {
  id: number
  start: number
  end: number
  title: string
}

// --- Series ---

export interface AbsSeriesResponse {
  results: AbsSeriesItem[]
  total: number
  limit: number
  page: number
}

export interface AbsSeriesItem {
  id: string
  name: string
  description: string | null
  books: AbsSeriesBook[]
  totalDuration: number
}

export interface AbsSeriesBook {
  id: string
  media: AbsBookMedia | null
  sequence: string | null
}

// --- Transfers ---

export type TransferStatus =
  | 'Pending'
  | 'DownloadingAudio'
  | 'UploadingToYoto'
  | 'AwaitingTranscode'
  | 'GeneratingIcons'
  | 'CreatingCard'
  | 'Completed'
  | 'Failed'
  | 'Cancelled'

export interface TransferResponse {
  id: string
  absLibraryItemId: string
  bookTitle: string
  bookAuthor: string | null
  seriesName: string | null
  seriesSequence: number | null
  status: TransferStatus
  progressPercent: number
  errorMessage: string | null
  ageRange: AgeRangeResponse
  yotoCardId: string | null
  createdAt: string
  completedAt: string | null
  tracks: TrackMappingResponse[]
}

export interface AgeRangeResponse {
  suggestedMin: number
  suggestedMax: number
  suggestionReason: string
  suggestionSource: string
  overrideMin: number | null
  overrideMax: number | null
  effectiveMin: number
  effectiveMax: number
}

export interface TrackMappingResponse {
  id: string
  chapterTitle: string
  chapterIndex: number
  duration: number
  isUploaded: boolean
  iconUrl: string | null
}

/** Where one track is on its way to Yoto, as the server reports it while a transfer runs. */
export type TrackPhase = 'Downloading' | 'Uploading' | 'Transcoding' | 'Uploaded' | 'Reused'

/** The latest thing the server said about one track. `percent` is Yoto's own, while it transcodes. */
export interface TrackLiveState {
  phase: TrackPhase
  percent: number | null
}

export interface TransferProgressUpdate {
  transferId: string
  status: TransferStatus
  progressPercent: number
  currentStep: string | null
  errorMessage: string | null
  trackId?: string | null
  trackPhase?: TrackPhase | null
  trackPercent?: number | null
}

// --- Batch Transfer (Phase 2) ---

export interface BatchTransferResponse {
  batchId: string
  totalBooks: number
  queued: number
  jobIds: string[]
}

// --- Age Suggestion ---

export interface AgeSuggestionResponse {
  suggestedMinAge: number
  suggestedMaxAge: number
  reason: string
  source: string
  signals: { signal: string; value: string; weight: number }[]
}

// --- Book Detail (combined endpoint response) ---

export interface BookDetailResponse {
  item: AbsLibraryItem
  ageSuggestion: AgeSuggestionResponse | null
  existingTransfer: {
    id: string
    status: TransferStatus
    yotoCardId: string | null
    completedAt: string | null
  } | null
}

// --- Yoto Cards (Phase 5) ---

export interface YotoCardSummary {
  cardId: string
  title: string | null
  metadata: YotoCardMetadata | null
  chapterCount: number
  trackCount: number
  fromAudioYotoShelf: boolean
  sourceBookTitle: string | null
  sourceBookAuthor: string | null
}

export interface YotoCardMetadata {
  author: string | null
  category: string | null
  description: string | null
  genre: string[] | null
  languages: string[] | null
  minAge: number | null
  maxAge: number | null
  readBy: string | null
  cover: { imageL: string | null } | null
}

export interface YotoCardDetail {
  cardId: string
  content: YotoCardContent | null
  metadata: YotoCardMetadata | null
}

export interface YotoCardContent {
  chapters: YotoChapter[]
  playbackType: string | null
}

export interface YotoChapter {
  key: string
  title: string
  tracks: YotoTrack[]
  display: { icon16x16: string | null } | null
}

export interface YotoTrack {
  key: string
  title: string
  trackUrl: string
  format: string | null
  duration: number | null
  fileSize: number | null
}
