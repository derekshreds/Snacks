pipeline {
  agent any

  stages {

    stage('Stage 1') {
      steps {
        timeout(time: 1, unit: 'MINUTES') {
          waitUntil {
            script {
              echo 'Testing build stage 1'
              return true
            }
          }
        }
      }
    }

    stage('Stage 2') {
      steps {
        timeout(time: 1, unit: 'MINUTES') {
          waitUntil {
            script {
              echo 'Testing build stage 2'
              return true
            }
          }
        }
      }
    }

    stage('Stage 3') {
      steps {
        timeout(time: 10, unit: 'SECONDS') {
          waitUntil {
            script {
              echo 'Testing build stage 3'
              return true
            }
          }
        }
      }
    }

  }
}
