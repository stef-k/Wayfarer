# TODO List

Each subtitle has a priority number with 1 being the first one to be implemented.

## Trusted Managers Mechanism, priority 1

Implement a trusted user/manager mechanism to allow managers see user location data.

1 Managers should initiate a request to add to an organisation or in a group of family members or friends and user must agree first.
  The Name of the group should be dynamic from a predefined list, lets say for starters [Organisation, Family, Friends].
2 Managers so there must be a solution where a manager can access such groups and users can accept to join and leave at any time.
3 Managers and users should be able to see pending invitations.
4 Managers and users should be able to see managed groups (Managers) and joined groups (Users)
5 Once the request/accept completed, managers should be able to see user's live location. I a view there should be a group/user picker and a map.
  It could be something like a vertical area on the left side for said pick actions and the rest and most area could contain the map showing locations.
6 Managers should also be able to see on map an entire group members locations.
7 Each manager should only be able to access and see data for groups he is in.
8 Many managers can belong to many groups.
9 Many users can belong to many groups.
10 Users should be able to only see what groups they joined.
11 Must be investigated if empty Groups should be auto deleted when the last member (either manager or user) leaves.
12 Groups would be only accessible from managers or users belonging to them, no public or not member accessible.
13 Groups could be created by Managers and/or Users.
14 If Group created by user, the creator is the owner and manages CRUD operations with the restriction that he cannot add members wihtout them accepting first as described in steps 1 to 5.
15 If Group created by manager and if manager adds more managers in a group then the group can be administrated by all managers.
16 All actions [invite, join, remove, decline, leave] should be recorded using the existing auditing service.
17 The feature at completion should contain all logic, actions and ui that will be able to administrate and use for all scenarios.

### Implementation List

* Add necessary database mechanism to link managers with users
* Create the UI in user's and manager's areas

## Geofencing, priority 3

### Implementation List

* Create the UI in both User and Manager areas to create and store geofence areas
* Implement geofence queries in User and Manager areas

## Manager Fleet Tracking System, priority 4

### Implementation List

* Create the UI in manager's area to allow managers track vehicle location data
* Create the necessary database mechanism to link managers with vehicles, a possible many to many table
* Design the system so that a manager can only add/remove vehicles from his organization which leads to:
* Managers and Vehicles should have an additional DB field Organization as a unique identifying field linking them
