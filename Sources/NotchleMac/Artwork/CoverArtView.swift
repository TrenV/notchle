import AppKit
import SwiftUI

/// An album cover from `ArtworkImageCache`. With `placeholder` a music-note tile stands in
/// while there is no image (History rows); without it nothing is drawn until the image is
/// there (answer screens), and it then fades in.
struct CoverArtView: View {
    let trackID: String
    let url: URL?
    let size: CGFloat
    let cornerRadius: CGFloat
    var placeholder = true
    @State private var image: NSImage?
    @State private var loadedFor: String?

    var body: some View {
        // No URL: no cover was ever resolved for this entry (offline, or an old entry).
        let current = url == nil ? nil
            : loadedFor == trackID ? image : ArtworkImageCache.shared.cachedImage(for: trackID)
        Group {
            if let current {
                Image(nsImage: current)
                    .resizable()
                    .interpolation(.high)
                    .aspectRatio(contentMode: .fill)
                    .frame(width: size, height: size)
                    .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
                    .overlay(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                        .stroke(Color.white.opacity(0.08), lineWidth: 0.5))
                    .shadow(color: .black.opacity(0.5), radius: size > 40 ? 6 : 2, y: size > 40 ? 2 : 1)
                    .transition(.opacity)
                    .accessibilityLabel("Album cover")
            } else if placeholder {
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .fill(Color.white.opacity(0.08))
                    .frame(width: size, height: size)
                    .overlay(Image(systemName: "music.note")
                        .font(.system(size: size * 0.42, weight: .semibold))
                        .foregroundStyle(NotchPalette.tertiaryText))
            }
        }
        .animation(.easeOut(duration: 0.25), value: current != nil)
        .task(id: "\(trackID)|\(url?.absoluteString ?? "")") {
            guard let url, ArtworkImageCache.shared.cachedImage(for: trackID) == nil else { return }
            let loaded = await ArtworkImageCache.shared.image(for: trackID, url: url)
            image = loaded
            loadedFor = trackID
        }
    }
}
