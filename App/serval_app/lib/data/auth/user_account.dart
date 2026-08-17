import 'auth_models.dart';

/// One account on this Server, as `GET/POST/PUT /api/auth/users` return it — the client half of
/// `Server/Serval.Server/Auth/AuthEndpoints.cs`'s `UserResponse`.
class UserAccount {
  const UserAccount({
    required this.username,
    required this.displayName,
    required this.role,
    required this.createdAt,
  });

  factory UserAccount.fromJson(Map<String, dynamic> json) => UserAccount(
    username: json['username'] as String,
    displayName: json['displayName'] as String,
    role: Role.fromJson(json['role'] as String),
    createdAt: DateTime.parse(json['createdAt'] as String),
  );

  final String username;
  final String displayName;
  final Role role;
  final DateTime createdAt;
}
