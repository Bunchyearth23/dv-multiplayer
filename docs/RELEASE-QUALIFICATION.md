# Qualification et release reproductibles

Cette procédure prépare W-018 sans lancer automatiquement Derail Valley.

## Baseline hors jeu

1. Exécuter `powershell -NoProfile -File tools/Validate.ps1` à la racine.
2. Archiver `tools/validation-latest.log`, le SHA Git et les versions jeu/BDVM/Multiplayer/mods.
3. Construire l'archive et vérifier la présence de `LICENSE` et `NOTICE`.
4. Refuser la qualification si le SHA n'est pas reproductible ou si un test échoue.

## Dossier de preuve

Créer un dossier horodaté contenant `manifest.txt`, un log host et un log par client. Le manifest indique SHA, versions, OS, CPU/GPU/RAM, VR, transport, joueurs/trains, durée, RTT/jitter/pertes et scénario. Synchroniser les horloges. Conserver les lignes `network_campaign`, `network_traffic` et `train_queue`.

| Axe | Valeurs minimales |
| --- | --- |
| joueurs | 2, puis 4, puis 8 seulement si retenu comme cible |
| réseau | baseline ; RTT 50/150/300 ms ; jitter 30/80 ms ; pertes 1/5 % |
| clients | non-VR/non-VR ; VR/non-VR ; VR/VR si supporté |
| lifecycle | join, late join, déconnexion en interaction, reconnexion, restart host, trois reloads |
| monde | trains éloignés, couple/uncouple, jobs/cargo, signaux/aiguillages, items/achats/licences |
| adapters | vanilla ; BDVM identique ; absent ; autre version ; erreur contrôlée |

Comparer host et clients sur compositions des trains, owners/états d'items, jobs, wallet, licences et aiguillages. Noter le premier instant de divergence et l'identifiant corrélé.

## Seuils candidats à confirmer

Ils ne deviennent des critères qu'après décision de Q-001 et première mesure : aucun overflow ; aucune divergence P0 persistante ; p95 frame ≤ 33 ms et p99 ≤ 50 ms ; mémoire stabilisée après warm-up ; correction train sans divergence durable ; reconnexion et late join sous leur deadline totale.

## Installation et rollback

Sauvegarder la partie et l'ancienne archive. Installer uniquement un artifact contenant `LICENSE` et `NOTICE`, sans mélanger deux versions. Pour rollback : arrêter toutes les instances, restaurer ensemble Multiplayer/API et les adapters compatibles, puis restaurer la sauvegarde de test si nécessaire.

## Actions humaines indispensables

- Décider Q-001 : joueurs, durée, matériel minimal, réseau et périmètre VR/mods.
- Fournir/installer les adapters BDVM ciblés et leurs `info.json`.
- Lancer les instances, connecter les contrôleurs VR et appliquer l'émulation réseau.
- Exécuter les scénarios, archiver les preuves et arbitrer les défauts P1/P2 acceptés.
