/// Where the source of the running Serval can be obtained.
///
/// AGPL section 13 owes a user reaching Serval over a network the source of the version they are
/// using, offered from the interface itself — not the project's front page, and not whatever the
/// default branch happens to be today.
///
/// **The revision is stamped in at build time**, from `SOURCE_REVISION`, which the Dockerfile
/// passes to `flutter build web` and the image workflow fills with the commit being built. That
/// works because the web App is compiled in the same image as the Server it ships beside, so the
/// commit the App was built from *is* the commit the Server is running. Nothing is asked of the
/// Server at runtime.
///
/// **A build made outside that workflow reports nothing**, and [url] falls back to the repository.
/// A local `flutter run` has no commit to name, and naming one would be a guess. The link is drawn
/// either way: an offer that depends on a value being present is not an offer.
class SourceOffer {
  const SourceOffer._();

  static const String repositoryUrl = 'https://github.com/Flickersoft/Serval';

  static const String license = 'AGPL-3.0-or-later';

  /// The commit this build was made from, or empty outside the image workflow.
  static const String revision = String.fromEnvironment('SOURCE_REVISION');

  /// The release this build is, as `major.minor.patch`, or empty outside the image workflow.
  static const String version = String.fromEnvironment('SERVAL_VERSION');

  /// [repositoryUrl] at [revision] where there is one, the repository itself otherwise.
  static String get url =>
      revision.isEmpty ? repositoryUrl : '$repositoryUrl/tree/$revision';

  /// The seven characters a person reads a commit by, or null when there is no revision.
  static String? get shortRevision => revision.isEmpty
      ? null
      : revision.substring(0, revision.length.clamp(0, 7));

  /// How this build names itself in one line, for the *Source* offer to sit beside.
  static String get label => labelFor(version, shortRevision);

  /// The pure form of [label], for a build stamped with [version] and [shortRevision].
  ///
  /// The version leads because it is what a person quotes, and the commit follows because it is
  /// what the offer is actually *for*: a version names a release, but only the commit identifies
  /// the build being run, which is what AGPL section 13 entitles a user to. Either may be missing
  /// — a local `flutter run` has neither — and what is left is the licence, which is never a guess.
  ///
  /// Separate from [label] because the stamps are `String.fromEnvironment` and so are fixed at
  /// compile time: under `flutter test` they are always empty, and a getter reading them directly
  /// can only ever exercise the one branch.
  static String labelFor(String version, String? shortRevision) {
    if (version.isEmpty) return shortRevision ?? license;
    return shortRevision == null ? version : '$version ($shortRevision)';
  }
}
