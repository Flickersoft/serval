/// Serval's read on what is happening on this camera right now.
///
/// The prose maps onto a `SceneDocument.description`, which is the whole of what
/// the vision model produces.
class SceneSummary {
  const SceneSummary({required this.text});

  final String text;
}
