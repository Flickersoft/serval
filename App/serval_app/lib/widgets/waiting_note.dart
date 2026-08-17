import 'package:flutter/widgets.dart';

import '../theme/nocturne.dart';

/// What a screen shows while its first read of the Server is in flight, and —
/// when [error] is set — what it says instead if that read failed.
class WaitingNote extends StatelessWidget {
  const WaitingNote({
    super.key,
    required this.message,
    this.error,
    this.padding = EdgeInsets.zero,
  });

  final String message;
  final String? error;
  final EdgeInsets padding;

  @override
  Widget build(BuildContext context) => Center(
    child: Padding(
      padding: padding,
      child: Text(
        error ?? message,
        textAlign: TextAlign.center,
        style: TextStyle(
          fontFamily: Nocturne.fontBody,
          fontSize: 13,
          color: Nocturne.mix(Nocturne.text, error == null ? 45 : 70),
        ),
      ),
    ),
  );
}
