# ASP.NET Core Ignite Server

This is a helm chart for [dradoaica/aspnetcore.ignite-server](https://hub.docker.com/repository/docker/dradoaica/aspnetcore.ignite-server/)

aspnetcore-ignite-server is an ASP.NET Core implementation of Apache Ignite server.

## Install

```console
helm install --name aspnetcore-ignite-server helm/charts/aspnetcore-ignite-server
```

## Configuration

| Parameter                       | Description                                                                                                    | Default                              |
| ------------------------------- |----------------------------------------------------------------------------------------------------------------|--------------------------------------|
| `nameOverride`                  | String to partially override ignite.fullname template with a string (will prepend the release name)            | `nil`                                |
| `fullnameOverride`              | String to fully override ignite.fullname template with a string                                                | `nil`                                |
| `image.repository`              | Image repository                                                                                               | `dradoaica/aspnetcore.ignite-server` |
| `image.tag`                     | Image tag                                                                                                      | `2.16`                               |
| `image.pullPolicy`              | Image pull policy                                                                                              | `IfNotPresent`                       |
| `replicaCount`                  | Number of pods for ignite applications                                                                         | `1`                                  |
| `rbac.create`                   | Whether or not to create RBAC items (e.g. role, role-binding)                                                  | `true`                               |
| `serviceAccount.create`         | Whether or not to create dedicated serviceAccount for ignite                                                   | `true`                               |
| `serviceAccount.name`           | If `serviceAccount.create` is enabled, what should the `serviceAccount` name be - otherwise randomly generated | `nil`                                |
| `env`                           | Dictionary (key/value) for additional environment for pod templates (if you need refs use envVars)             | `{ ... }`                            |
| `envVars`                       | Array of Dictionaries (key/value) for additional environment for pod templates                                 | `nil`                                |
| `envFrom`                       | Array of Dictionaries (key/value) for additional environment from secrets/configmaps for pod templates         | `nil`                                |
| `extraInitContainers`           | additional Init Containers to run in the pods                                                                  | `[]`                                 |
| `extraContainers`               | additional containers to run in the pods                                                                       | `[]`                                 |
| `persistence.workVolume`        | Persistent volume definition for work storage                                                                  | `{ "size": "8Gi" }`                  |
| `extraVolumes`                  | Extra volumes                                                                                                  | `nil`                                |
| `extraVolumeMounts`             | Mount extra volume(s)                                                                                          | `nil`                                |
| `resources`                     | Pod request/limits                                                                                             | `{}`                                 |
| `nodeSelector`                  | Node selector for ignite application                                                                           | `{}`                                 |
| `tolerations`                   | Node tolerations for ignite application                                                                        | `[]`                                 |
| `affinity`                      | Node affinity for ignite application                                                                           | `{}`                                 |
| `priorityClassName`             | Pod Priority Class Name for ignite application                                                                 | `""`                                 |
